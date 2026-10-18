using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class Post
{
    [Key]
    public int PostId { get; set; }

    [Required(ErrorMessage = "العنوان مطلوب")]
    [StringLength(200)]
    [Display(Name = "العنوان")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "المحتوى مطلوب")]
    [Display(Name = "المحتوى")]
    public string Content { get; set; } = string.Empty;

    [Display(Name = "الصورة")]
    public string? ImageUrl { get; set; }

    [Display(Name = "الكاتب")]
    public string Author { get; set; } = string.Empty;

    [Display(Name = "النوع")]
    [StringLength(40)]
    public string PostType { get; set; } = "Post";
    // Post, News

    [Display(Name = "الحالة")]
    [StringLength(40)]
    public string Status { get; set; } = "Draft";
    // Draft, Published, Archived

    [Display(Name = "تاريخ النشر")]
    public DateTime? PublishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }
}
