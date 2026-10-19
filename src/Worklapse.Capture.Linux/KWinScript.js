// Loaded into KWin (Plasma 6) by KWinSession. On Wayland only the compositor knows which window has focus and how the
// screens are laid out, so this script reports both to Worklapse over D-Bus whenever they change. KWinSession fills
// in the placeholders: Worklapse's unique D-Bus name, and the object path and interface it listens on.
//
// Each report is one JSON string: {"kind": "window", ...} or {"kind": "screens", "screens": [...]}. Coordinates are
// KWin's logical ones (scaled screens are smaller than their pixel size).

const service = "%SERVICE%";
const path = "%PATH%";
const iface = "%INTERFACE%";

function send(kind, data) {
    data.kind = kind;
    callDBus(service, path, iface, "Update", JSON.stringify(data));
}

// --- the active window -----------------------------------------------------------------------------

function describe(window) {
    if (!window) {
        return { active: false };
    }
    const g = window.frameGeometry;
    return {
        active: true,
        pid: window.pid,
        caption: window.caption,
        resourceClass: window.resourceClass,
        desktopFile: window.desktopFileName,
        x: g.x,
        y: g.y,
        width: g.width,
        height: g.height,
    };
}

function reportWindow() {
    send("window", describe(workspace.activeWindow));
}

// While a window is dragged or resized its geometry changes constantly; report once it settles.
function onGeometryChanged() {
    const window = workspace.activeWindow;
    if (window && !window.move && !window.resize) {
        reportWindow();
    }
}

let watched = null;

function watch(window) {
    if (watched) {
        try {
            watched.captionChanged.disconnect(reportWindow);
            watched.frameGeometryChanged.disconnect(onGeometryChanged);
            watched.interactiveMoveResizeFinished.disconnect(reportWindow);
        } catch (e) {
            // The window is gone, and its connections with it.
        }
    }
    watched = window;
    if (window) {
        window.captionChanged.connect(reportWindow);
        window.frameGeometryChanged.connect(onGeometryChanged);
        window.interactiveMoveResizeFinished.connect(reportWindow);
    }
    reportWindow();
}

// --- the screens -----------------------------------------------------------------------------------

function reportScreens() {
    send("screens", {
        screens: workspace.screens.map(function (output) {
            const g = output.geometry;
            return {
                name: output.name,
                manufacturer: output.manufacturer || "",
                model: output.model || "",
                serial: output.serialNumber || "",
                x: g.x,
                y: g.y,
                width: g.width,
                height: g.height,
                scale: output.devicePixelRatio,
            };
        }),
    });
}

workspace.screensChanged.connect(reportScreens);
workspace.virtualScreenGeometryChanged.connect(reportScreens);
workspace.windowActivated.connect(watch);

reportScreens();
watch(workspace.activeWindow);
