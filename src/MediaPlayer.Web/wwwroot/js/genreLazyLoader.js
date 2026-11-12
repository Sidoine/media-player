let observer;
let loading = false;

export function observe(element, dotnet) {
    disconnect();
    if (!element) return;

    observer = new IntersectionObserver(async entries => {
        if (loading || !entries.some(entry => entry.isIntersecting)) return;
        loading = true;
        try {
            await dotnet.invokeMethodAsync("LoadMoreGenreRowsAsync");
        } catch {
            // The Blazor circuit may close while a batch is loading.
        } finally {
            loading = false;
            if (observer && element.isConnected) {
                observer.unobserve(element);
                requestAnimationFrame(() => observer?.observe(element));
            }
        }
    }, { rootMargin: "700px 0px" });

    observer.observe(element);
}

export function disconnect() {
    observer?.disconnect();
    observer = undefined;
    loading = false;
}
