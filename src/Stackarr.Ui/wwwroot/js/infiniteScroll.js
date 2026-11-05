// Infinite-scroll helper used by the reusable <InfiniteScroll> component.
// Observes a sentinel element and calls back into .NET when it nears the viewport.
const observers = new Map();

export function observe(element, dotNetRef) {
    if (!element) return;

    unobserve(element);

    const observer = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            if (entry.isIntersecting) {
                dotNetRef.invokeMethodAsync('OnIntersect').catch(() => { /* circuit gone */ });
            }
        }
    }, { rootMargin: '400px 0px' });

    observer.observe(element);
    observers.set(element, observer);
}

export function unobserve(element) {
    const observer = observers.get(element);
    if (observer) {
        observer.disconnect();
        observers.delete(element);
    }
}
