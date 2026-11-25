window.mediaSetup = {
    waitForRestart() {
        const retry = async () => {
            try {
                const response = await fetch("/api/users/status", { cache: "no-store" });
                if (response.ok) {
                    window.location.replace("/");
                    return;
                }
            } catch { }
            window.setTimeout(retry, 700);
        };
        window.setTimeout(retry, 900);
    }
};
