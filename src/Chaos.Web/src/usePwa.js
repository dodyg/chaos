import { useEffect, useState } from "react";

export function usePwa() {
  const [install, setInstall] = useState(null);
  const [update, setUpdate] = useState(null);

  useEffect(() => {
    const onInstall = (event) => {
      event.preventDefault();
      setInstall(event);
    };
    const installed = () => setInstall(null);
    addEventListener("beforeinstallprompt", onInstall);
    addEventListener("appinstalled", installed);
    return () => {
      removeEventListener("beforeinstallprompt", onInstall);
      removeEventListener("appinstalled", installed);
    };
  }, []);

  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    const serviceWorker = navigator.serviceWorker;
    let registration;
    let installing;
    let closed = false;
    let reloading = false;
    const inspect = () => {
      if (registration.waiting && serviceWorker.controller) setUpdate(registration.waiting);
    };
    const found = () => {
      installing?.removeEventListener("statechange", inspect);
      installing = registration.installing;
      installing?.addEventListener("statechange", inspect);
    };
    const changed = () => {
      if (reloading) location.reload();
    };
    const check = () => {
      if (navigator.onLine && document.visibilityState === "visible") {
        void registration?.update().catch(() => undefined);
      }
    };
    serviceWorker.addEventListener("controllerchange", changed);
    document.addEventListener("visibilitychange", check);
    serviceWorker
      .register("/app/sw.js")
      .then((result) => {
        if (closed) return;
        registration = result;
        registration.addEventListener("updatefound", found);
        found();
        inspect();
      })
      .catch((error) => console.warn("Chaos offline shell could not start.", error));

    const activate = () => {
      reloading = true;
    };
    addEventListener("chaos-reload", activate);
    return () => {
      closed = true;
      registration?.removeEventListener("updatefound", found);
      installing?.removeEventListener("statechange", inspect);
      serviceWorker.removeEventListener("controllerchange", changed);
      document.removeEventListener("visibilitychange", check);
      removeEventListener("chaos-reload", activate);
    };
  }, []);

  return {
    offerInstall: install
      ? async () => {
          setInstall(null);
          await install.prompt();
        }
      : null,
    reloadForUpdate: update
      ? () => {
          dispatchEvent(new Event("chaos-reload"));
          update.postMessage("activate-update");
        }
      : null,
  };
}
