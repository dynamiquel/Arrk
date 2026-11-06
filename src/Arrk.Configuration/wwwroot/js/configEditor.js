// Config editor textarea helpers
window.ce = {
    getPos: function (el) {
        return { start: el.selectionStart, end: el.selectionEnd };
    },

    getValue: function (el) {
        return el.value;
    },

    setPos: function (el, pos) {
        el.focus();
        el.selectionStart = pos;
        el.selectionEnd = pos;
    },

    insertText: function (el, text) {
        var start = el.selectionStart;
        var end = el.selectionEnd;
        var before = el.value.substring(0, start);
        var after = el.value.substring(end);
        el.value = before + text + after;
        el.selectionStart = start + text.length;
        el.selectionEnd = start + text.length;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.focus();
    },

    scrollSelected: function (dropdown, index) {
        if (!dropdown) return;
        var items = dropdown.querySelectorAll('.st-config-suggestion');
        if (items[index]) {
            items[index].scrollIntoView({ block: 'nearest' });
        }
    }
};
