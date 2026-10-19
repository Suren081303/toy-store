// Small enhancements. Every page also works without this script (links go to normal pages).
(function () {
    'use strict';

    // The anti-forgery token that the layout puts in a <meta> tag.
    function tokenHeaders() {
        var token = document.querySelector('meta[name="request-verification-token"]');
        return { 'RequestVerificationToken': token ? token.content : '' };
    }

    // The little message bar ("Added to cart", with a link to the cart).
    var toastTimer = 0;
    function showToast(message, withCartLink) {
        var toast = document.querySelector('[data-toast]');
        if (!toast) {
            return;
        }

        toast.textContent = message;
        if (withCartLink) {
            var link = document.createElement('a');
            link.href = toast.getAttribute('data-cart-url');
            link.textContent = toast.getAttribute('data-view-cart');
            toast.appendChild(link);
        }

        toast.hidden = false;
        window.clearTimeout(toastTimer);
        toastTimer = window.setTimeout(function () {
            toast.hidden = true;
        }, 4000);
    }

    // The number on the cart icons (header and tab bar).
    function setCartCount(count) {
        document.querySelectorAll('[data-cart-count]').forEach(function (badge) {
            badge.textContent = count;
            badge.hidden = count === 0;
        });
    }

    document.addEventListener('click', function (event) {
        // Open a dialog: <a data-open="dialog-id"> (menu drawer, search sheet, filters sheet).
        var opener = event.target.closest('[data-open]');
        if (opener) {
            var dialog = document.getElementById(opener.getAttribute('data-open'));
            if (dialog && typeof dialog.showModal === 'function') {
                event.preventDefault();
                dialog.showModal();
                var field = dialog.querySelector('input[type="search"]');
                if (field) {
                    field.focus();
                }
            }
            return;
        }

        // Close a dialog: the close button, or a click on the dimmed area around it.
        var closer = event.target.closest('[data-close]');
        if (closer) {
            closer.closest('dialog').close();
            return;
        }
        if (event.target.tagName === 'DIALOG') {
            event.target.close();
            return;
        }

        // Quantity stepper on the product page: the - and + buttons change the number box within its min and max.
        var qtyStep = event.target.closest('[data-qty-step]');
        if (qtyStep) {
            var qtyInput = qtyStep.closest('[data-qty]').querySelector('input');
            var min = Number(qtyInput.min || 1);
            var max = Number(qtyInput.max || 99);
            var next = Number(qtyInput.value || min) + Number(qtyStep.getAttribute('data-qty-step'));
            qtyInput.value = Math.max(min, Math.min(max, next));
            return;
        }

        // Show/hide the password in a password field.
        var passwordToggle = event.target.closest('[data-toggle-password]');
        if (passwordToggle) {
            var passwordInput = passwordToggle.closest('.pw').querySelector('input');
            var show = passwordInput.type === 'password';
            passwordInput.type = show ? 'text' : 'password';
            passwordToggle.setAttribute('aria-pressed', show ? 'true' : 'false');
            return;
        }

        // Heart button: add/remove a favorite (saved in the account, or in a cookie for guests).
        var heart = event.target.closest('[data-fav-toggle]');
        if (heart) {
            event.preventDefault();
            heart.setAttribute('aria-busy', 'true');

            var language = document.documentElement.lang;
            var token = document.querySelector('meta[name="request-verification-token"]');
            var form = new FormData();
            form.append('productId', heart.getAttribute('data-fav-toggle'));

            fetch('/' + language + '/Favorites/Toggle', {
                method: 'POST',
                headers: { 'RequestVerificationToken': token ? token.content : '', 'Accept': 'application/json' },
                body: form
            })
                .then(function (response) {
                    if (response.status === 401) {
                        // The session ended: sign in again and come back here.
                        window.location.href = '/' + language + '/Account/Login?returnUrl=' +
                            encodeURIComponent(window.location.pathname + window.location.search);
                        return null;
                    }
                    return response.ok ? response.json() : null;
                })
                .then(function (data) {
                    heart.removeAttribute('aria-busy');
                    if (data === null) {
                        return;
                    }

                    heart.classList.toggle('is-active', data.isFavorite);
                    heart.setAttribute('aria-pressed', data.isFavorite ? 'true' : 'false');

                    // On the Favorites page an un-hearted product leaves the list.
                    if (!data.isFavorite && document.querySelector('[data-favorites-page]')) {
                        var card = heart.closest('.card');
                        if (card) {
                            card.remove();
                        }
                    }
                });
            return;
        }

        // "Load more": fetch the next page and add its products to the grid.
        var more = event.target.closest('[data-load-more]');
        if (more) {
            event.preventDefault();
            more.setAttribute('aria-busy', 'true');

            fetch(more.href)
                .then(function (response) {
                    if (!response.ok) {
                        window.location.href = more.href;
                        return null;
                    }
                    return response.text();
                })
                .then(function (html) {
                    if (html === null) {
                        return;
                    }

                    var page = new DOMParser().parseFromString(html, 'text/html');
                    var grid = document.querySelector('[data-product-grid]');
                    page.querySelectorAll('[data-product-grid] > *').forEach(function (item) {
                        grid.appendChild(document.importNode(item, true));
                    });

                    var box = document.querySelector('[data-load-more-box]');
                    var nextBox = page.querySelector('[data-load-more-box]');
                    if (nextBox) {
                        box.replaceWith(document.importNode(nextBox, true));
                    } else {
                        box.remove();
                    }
                });
        }
    });

    document.addEventListener('submit', function (event) {
        var form = event.target;

        // Forms that must be sent only once (the checkout): the buttons are switched off right after the click.
        if (form.matches('[data-once]')) {
            var onceButtons = Array.from(form.querySelectorAll('button[type="submit"]'));
            if (form.id) {
                onceButtons = onceButtons.concat(Array.from(document.querySelectorAll('button[form="' + form.id + '"]')));
            }
            window.setTimeout(function () {
                onceButtons.forEach(function (button) {
                    button.disabled = true;
                });
            }, 0);
            return;
        }

        // "Add to cart" without leaving the page.
        if (form.matches('[data-cart-add]')) {
            event.preventDefault();

            // The form's own buttons, and (on the product page) the sticky-bar button that points to the form with form="id".
            var buttons = Array.from(form.querySelectorAll('button[type="submit"]'));
            if (form.id) {
                buttons = buttons.concat(Array.from(document.querySelectorAll('button[form="' + form.id + '"]')));
            }
            buttons.forEach(function (button) {
                button.setAttribute('aria-busy', 'true');
            });

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                headers: Object.assign({ 'Accept': 'application/json' }, tokenHeaders())
            })
                .then(function (response) {
                    return response.ok ? response.json() : null;
                })
                .then(function (data) {
                    buttons.forEach(function (button) {
                        button.removeAttribute('aria-busy');
                    });

                    if (data === null) {
                        // Something went wrong: do it the plain way (the server answers with the cart page).
                        form.submit();
                        return;
                    }

                    setCartCount(data.count);
                    showToast(data.message, data.status === 'Added' || data.status === 'LimitReached');
                });
            return;
        }

        // Cart page: +, - and Remove update the page in place (scroll position stays).
        if (form.matches('[data-cart-form]')) {
            event.preventDefault();

            var cart = document.querySelector('[data-cart]');
            cart.setAttribute('aria-busy', 'true');

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form, event.submitter),
                headers: tokenHeaders()
            })
                .then(function (response) {
                    // The server redirects to the cart page, so this is the new cart page.
                    return response.ok ? response.text() : null;
                })
                .then(function (html) {
                    var fresh = html === null ? null : new DOMParser().parseFromString(html, 'text/html').querySelector('[data-cart]');
                    if (fresh === null) {
                        window.location.reload();
                        return;
                    }

                    cart.innerHTML = fresh.innerHTML;
                    cart.removeAttribute('aria-busy');
                    setCartCount(Number(fresh.getAttribute('data-cart-items')));
                    document.body.classList.toggle('has-buybar', cart.querySelector('.buybar') !== null);
                });
        }
    });

    // Coming back to the checkout with the browser's Back button: the buttons work again.
    window.addEventListener('pageshow', function (event) {
        if (event.persisted) {
            document.querySelectorAll('[data-once]').forEach(function (form) {
                form.querySelectorAll('button[type="submit"]').forEach(function (button) {
                    button.disabled = false;
                });
            });
            document.querySelectorAll('button[form]').forEach(function (button) {
                button.disabled = false;
            });
        }
    });

    // Product list filters: leave empty and default fields out of the address (/Products?age=2 instead of ?q=&category=&age=2&sort=Recommended).
    var filterForm = document.getElementById('filter-form');
    if (filterForm) {
        filterForm.addEventListener('submit', function () {
            filterForm.querySelectorAll('input[name], select[name]').forEach(function (control) {
                if (control.value === '' || (control.name === 'sort' && control.value === 'Recommended')) {
                    control.disabled = true;
                }
            });
        });
    }

    // Product gallery: dots/thumbnails follow the swipe and scroll to the chosen photo.
    document.querySelectorAll('[data-gallery]').forEach(function (gallery) {
        var track = gallery.querySelector('[data-gallery-track]');
        var buttons = gallery.querySelectorAll('[data-gallery-go]');

        function update() {
            var index = Math.round(track.scrollLeft / track.clientWidth);
            buttons.forEach(function (button) {
                button.classList.toggle('is-active', Number(button.getAttribute('data-gallery-go')) === index);
            });
        }

        track.addEventListener('scroll', function () {
            window.requestAnimationFrame(update);
        }, { passive: true });

        buttons.forEach(function (button) {
            button.addEventListener('click', function () {
                track.scrollTo({ left: Number(button.getAttribute('data-gallery-go')) * track.clientWidth, behavior: 'smooth' });
            });
        });

        update();
    });
})();
