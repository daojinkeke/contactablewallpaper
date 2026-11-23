namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 注入到每个文档的交互桥脚本（06-interaction-spec.md §6）。
    /// 脚本只负责把宿主下发的规范化输入合成为页面 DOM 事件：
    /// 坐标已由宿主按 CSS 像素换算，脚本内不做任何换算。
    /// 默认开启合成，无需页面先发送 contactable.ready。
    /// </summary>
    internal static class InteractionBridgeScript
    {
        public const string Script = @"
(function () {
    'use strict';
    if (window.__contactableBridgeInstalled) {
        return;
    }
    window.__contactableBridgeInstalled = true;

    var PROTOCOL_VERSION = 1;
    var INPUT_TYPE = 'contactable.input';
    var READY_TYPE = 'contactable.ready';
    var HIT_REGIONS_TYPE = 'contactable.hitRegions';
    var SET_INTERACTIVE_TYPE = 'contactable.setInteractive';
    var LEAVE_TYPE = 'contactable.leave';
    var listeners = [];
    var synthesisEnabled = true;
    var lastTarget = null;

    // 原生事件（isTrusted）同样更新最后目标元素，离开复位才能落在正确元素上。
    document.addEventListener('pointermove', function (event) {
        if (event.isTrusted && event.target) {
            lastTarget = event.target;
        }
    }, true);

    // 浏览器原生 pointerleave 上报宿主：宿主据此跳过合成兜底，避免双发。
    function reportNativeLeave(event) {
        if (!event.isTrusted) {
            return;
        }
        post({ type: LEAVE_TYPE, version: PROTOCOL_VERSION });
    }
    window.addEventListener('pointerleave', reportNativeLeave, true);
    document.addEventListener('pointerleave', reportNativeLeave, true);

    function post(message) {
        try {
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                window.chrome.webview.postMessage(message);
            }
        } catch (e) {
        }
    }

    function targetAt(x, y) {
        if (typeof x !== 'number' || typeof y !== 'number') {
            return document.body || document.documentElement;
        }
        var element = document.elementFromPoint(x, y);
        return element || document.body || document.documentElement;
    }

    function buttonIndex(button) {
        if (button === 'left') {
            return 0;
        }
        if (button === 'middle') {
            return 1;
        }
        if (button === 'right') {
            return 2;
        }
        return -1;
    }

    function emitPointer(kind, data) {
        var element = targetAt(data.x, data.y);
        if (!element) {
            return;
        }
        lastTarget = element;

        try {
            element.dispatchEvent(new PointerEvent(kind, {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: data.x,
                clientY: data.y,
                pointerId: 1,
                pointerType: 'mouse',
                isPrimary: true,
                button: buttonIndex(data.button),
                buttons: data.buttons || 0,
                view: window
            }));
        } catch (e) {
        }

        // 兼容只监听传统 mouse 事件的页面；浏览器原生指针事件同样会派发这两个。
        var legacy = kind === 'pointermove' ? 'mousemove' : (kind === 'pointerdown' ? 'mousedown' : 'mouseup');
        try {
            element.dispatchEvent(new MouseEvent(legacy, {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: data.x,
                clientY: data.y,
                button: buttonIndex(data.button),
                buttons: data.buttons || 0,
                view: window
            }));
        } catch (e) {
        }
    }

    function emitWheel(data) {
        var element = targetAt(data.x, data.y);
        if (!element) {
            return;
        }
        // Windows 正 delta 表示滚轮远离用户（上滚），DOM deltaY 方向相反。
        try {
            element.dispatchEvent(new WheelEvent('wheel', {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: data.x,
                clientY: data.y,
                deltaX: 0,
                deltaY: -(data.wheelDelta || 0),
                deltaMode: 0,
                view: window
            }));
        } catch (e) {
        }
    }

    function emitKey(kind, data) {
        var modifiers = data.modifiers || {};
        var target = document.activeElement || document.body || document.documentElement;
        if (!target) {
            return;
        }
        try {
            target.dispatchEvent(new KeyboardEvent(kind, {
                bubbles: true,
                cancelable: true,
                composed: true,
                key: data.key || '',
                code: data.code || '',
                ctrlKey: !!modifiers.ctrl,
                shiftKey: !!modifiers.shift,
                altKey: !!modifiers.alt,
                metaKey: !!modifiers.meta,
                view: window
            }));
        } catch (e) {
        }
    }

    function emitLeave(data) {
        var init = {
            bubbles: false,
            cancelable: false,
            composed: true,
            clientX: typeof data.x === 'number' ? data.x : 0,
            clientY: typeof data.y === 'number' ? data.y : 0,
            pointerId: 1,
            pointerType: 'mouse',
            isPrimary: true,
            button: -1,
            buttons: 0,
            view: window
        };
        var element = lastTarget || document.documentElement;
        try {
            element.dispatchEvent(new PointerEvent('pointerleave', init));
            element.dispatchEvent(new MouseEvent('mouseleave', init));
        } catch (e) {
        }
        try {
            window.dispatchEvent(new PointerEvent('pointerleave', init));
        } catch (e) {
        }
        lastTarget = null;
    }

    function dispatchInput(data) {
        if (!data || typeof data !== 'object') {
            return;
        }
        // 版本不兼容时页面忽略，避免字段语义漂移。
        if (data.type !== INPUT_TYPE || data.version !== PROTOCOL_VERSION) {
            return;
        }
        if (!synthesisEnabled) {
            return;
        }

        switch (data.kind) {
            case 'pointermove':
            case 'pointerdown':
            case 'pointerup':
                emitPointer(data.kind, data);
                break;
            case 'wheel':
                emitWheel(data);
                break;
            case 'keydown':
            case 'keyup':
                emitKey(data.kind, data);
                break;
            case 'pointerleave':
                emitLeave(data);
                break;
            default:
                break;
        }

        try {
            window.dispatchEvent(new CustomEvent(INPUT_TYPE, { detail: data }));
        } catch (e) {
        }
        for (var i = 0; i < listeners.length; i++) {
            try {
                listeners[i](data);
            } catch (e) {
            }
        }
    }

    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === 'function') {
        window.chrome.webview.addEventListener('message', function (event) {
            dispatchInput(event.data);
        });
    }

    window.contactable = {
        version: PROTOCOL_VERSION,
        addListener: function (listener) {
            if (typeof listener === 'function' && listeners.indexOf(listener) < 0) {
                listeners.push(listener);
            }
        },
        removeListener: function (listener) {
            var index = listeners.indexOf(listener);
            if (index >= 0) {
                listeners.splice(index, 1);
            }
        },
        // 可选握手：页面主动声明支持；宿主不再把它作为推送前提。
        ready: function () {
            post({ type: READY_TYPE, version: PROTOCOL_VERSION });
        },
        // 页面显式关闭/恢复宿主合成。
        setInteractive: function (enabled) {
            synthesisEnabled = !!enabled;
            post({ type: SET_INTERACTIVE_TYPE, version: PROTOCOL_VERSION, enabled: !!enabled });
        },
        setHitRegions: function (regions) {
            post({
                type: HIT_REGIONS_TYPE,
                version: PROTOCOL_VERSION,
                regions: Array.isArray(regions) ? regions : []
            });
        }
    };
})();
";
    }
}
