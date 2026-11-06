// Keeps the assistant's short thread pinned to the newest line after a turn renders. Scoped to the
// thread element so the page or the card sheet never scrolls.
window.arrkAssistantScroll = function () {
    const thread = document.getElementById('st-assistant-thread');
    if (thread) thread.scrollTop = thread.scrollHeight;
};
