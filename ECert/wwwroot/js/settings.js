// Keep the active tab visible when the strip overflows on narrow screens.
(function () {
    var strip = document.querySelector('.settings-tabs');
    if (!strip) return;

    var active = strip.querySelector('.settings-tab.is-active');
    if (!active || strip.scrollWidth <= strip.clientWidth) return;

    active.scrollIntoView({ block: 'nearest', inline: 'center' });
})();
