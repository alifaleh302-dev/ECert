using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Idempotent repair for certificate designs saved while the designer numbered partner slots with
/// one counter shared by logos and signatures. That bug numbered logos 0,1,… and then let the
/// signatures continue at 2,3,…, pushing every signature past the course's partner count so it
/// resolved to nothing and never appeared on the public verification page. The intended scheme
/// numbers each element type from 0 independently, because a partner's logo and signature elements
/// both index into the same course partner list (partner #1 ↔ logo element #1 and signature #1).
///
/// The two schemes are distinguishable without guessing: a correctly numbered design that has both
/// kinds of element always shares a slot across them (both start at 0, and the slot picker only
/// offers 0..count-1 per type), whereas the shared counter could never assign the same slot twice,
/// so a buggy design's logo and signature slots are disjoint. The repair therefore only rewrites a
/// design whose logo slots and signature slots do not overlap.
/// </summary>
public sealed class CertificatePartnerSlotRepairService
{
    private readonly ECertDbContext _db;

    public CertificatePartnerSlotRepairService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        var partnerElements = await _db.CertificateDesignElements
            .Where(e => e.ElementType == "partner_logo" || e.ElementType == "partner_signature")
            .ToListAsync();

        if (partnerElements.Count == 0) return;

        var changed = false;
        foreach (var design in partnerElements.GroupBy(e => e.CertificateDesignId))
        {
            var logos = design.Where(e => e.ElementType == "partner_logo").ToList();
            var signatures = design.Where(e => e.ElementType == "partner_signature").ToList();
            if (logos.Count == 0 || signatures.Count == 0) continue;

            var logoSlots = logos.Select(e => CertificateDesignService.ParsePartnerSlot(e.FieldKey)).ToHashSet();
            if (logoSlots.Overlaps(signatures.Select(e => CertificateDesignService.ParsePartnerSlot(e.FieldKey))))
                continue; // Correctly numbered design: logo and signature share a slot.

            changed |= CompactSlots(logos);
            changed |= CompactSlots(signatures);
        }

        if (changed)
            await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Rewrites one element type's slots to 0..k-1 in display order. Elements that already shared a
    /// slot keep sharing it, so a partner duplicated across two elements is preserved.
    /// </summary>
    private static bool CompactSlots(IReadOnlyList<CertificateDesignElement> elements)
    {
        var changed = false;
        var slotMap = new Dictionary<int, int>();

        foreach (var element in elements.OrderBy(e => e.SortOrder).ThenBy(e => e.CertificateDesignElementId))
        {
            var slot = CertificateDesignService.ParsePartnerSlot(element.FieldKey);
            if (!slotMap.TryGetValue(slot, out var mapped))
            {
                mapped = slotMap.Count;
                slotMap[slot] = mapped;
            }

            var value = mapped.ToString();
            if (!string.Equals(element.FieldKey, value, StringComparison.Ordinal))
            {
                element.FieldKey = value;
                changed = true;
            }
        }

        return changed;
    }
}
