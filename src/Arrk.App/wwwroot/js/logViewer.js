// Log viewer helpers
window.lv = {
    scrollToBottom: function (selector) {
        var el = document.querySelector(selector);
        if (el) {
            el.scrollTop = el.scrollHeight;
        }
    }
};