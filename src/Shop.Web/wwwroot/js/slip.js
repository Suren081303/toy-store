// Packing slip page: the Print button opens the browser's print dialog. The page itself works without this script (Ctrl+P).
(function () {
    'use strict';

    document.addEventListener('click', function (event) {
        if (event.target.closest('[data-print]')) {
            window.print();
        }
    });
})();
