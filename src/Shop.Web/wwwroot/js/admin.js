// Admin panel: asks "are you sure?" before the buttons that cancel, reject or record money. Everything else works without this script.
(function () {
    'use strict';

    document.addEventListener('click', function (event) {
        var button = event.target.closest('[data-confirm]');
        if (button && !window.confirm(button.getAttribute('data-confirm'))) {
            event.preventDefault();
        }
    });

    // The "select all" box in the product list ticks or unticks every product box on the page.
    document.addEventListener('change', function (event) {
        var all = event.target.closest('[data-select-all]');
        if (all) {
            document.querySelectorAll('input[name="ids"]').forEach(function (box) { box.checked = all.checked; });
        }
    });

    // The bell: ask the server for the number of unread notifications every 120 seconds, but only while this page is visible
    // (a hidden tab does not need it, and it is refreshed at once when the admin comes back after a longer time).
    var bell = document.querySelector('[data-bell]');
    if (bell) {
        var badge = bell.querySelector('[data-bell-count]');
        var seconds = Number(bell.getAttribute('data-interval')) || 120;
        var baseTitle = document.title.replace(/^\(\d+\)\s+/, '');
        var lastRefresh = Date.now();

        var show = function (count) {
            badge.textContent = count;
            badge.hidden = count === 0;
            document.title = (count > 0 ? '(' + count + ') ' : '') + baseTitle;
        };

        var refresh = function () {
            lastRefresh = Date.now();
            fetch(bell.getAttribute('data-url'), { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
                .then(function (response) { return response.ok ? response.json() : null; })
                .then(function (data) {
                    if (data) {
                        show(data.unread);
                    }
                });
        };

        show(Number(badge.textContent) || 0);

        window.setInterval(function () {
            if (document.visibilityState === 'visible') {
                refresh();
            }
        }, seconds * 1000);

        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'visible' && Date.now() - lastRefresh > seconds * 1000) {
                refresh();
            }
        });
    }
})();
