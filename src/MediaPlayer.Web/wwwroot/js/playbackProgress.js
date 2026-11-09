const listeners = new WeakMap();

export function attach(video, dotnet, startAt) {
    const resume = () => {
        if (Number.isFinite(startAt) && startAt > 0 && startAt < video.duration - 5) {
            video.currentTime = startAt;
        }
    };

    const save = () => {
        if (!Number.isFinite(video.duration) || video.duration <= 0) return;
        dotnet.invokeMethodAsync("SavePlaybackProgress", video.currentTime, video.duration).catch(() => {});
    };
    const onTimeUpdate = () => {
        if (video.paused || video.seeking) return;
        if (video.currentTime - lastSaved >= 5) {
            lastSaved = video.currentTime;
            save();
        }
    };
    let lastSaved = Number.isFinite(video.currentTime) ? video.currentTime : 0;
    const onSeeked = () => { lastSaved = video.currentTime; save(); };
    const onPause = () => save();
    const onEnded = () => save();
    const onPageHide = () => save();

    video.addEventListener("loadedmetadata", resume, { once: true });
    video.addEventListener("timeupdate", onTimeUpdate);
    video.addEventListener("seeked", onSeeked);
    video.addEventListener("pause", onPause);
    video.addEventListener("ended", onEnded);
    window.addEventListener("pagehide", onPageHide);
    listeners.set(video, { resume, onTimeUpdate, onSeeked, onPause, onEnded, onPageHide });
    if (video.readyState >= 1) resume();
}

export function detach(video) {
    const handlers = listeners.get(video);
    if (!handlers) return;
    video.removeEventListener("loadedmetadata", handlers.resume);
    video.removeEventListener("timeupdate", handlers.onTimeUpdate);
    video.removeEventListener("seeked", handlers.onSeeked);
    video.removeEventListener("pause", handlers.onPause);
    video.removeEventListener("ended", handlers.onEnded);
    window.removeEventListener("pagehide", handlers.onPageHide);
    listeners.delete(video);
}
