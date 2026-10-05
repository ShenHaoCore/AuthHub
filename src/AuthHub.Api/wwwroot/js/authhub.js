/* ==========================================================================
   AuthHub 管理后台交互脚本（原生 JS，无框架、无外部依赖）

   为什么必须有这个文件：
     本站 CSP 是 script-src 'self'，行内 <script> 与 on* 事件属性都会被浏览器
     静默拦截。因此所有交互只能通过这里注册的 addEventListener 完成。

   覆盖的交互（均为渐进增强，页面在无 JS 时仍可读、表单仍可提交）：
     1. 移动端侧边栏开合
     2. 顶栏用户菜单
     3. <dialog> 模态：打开 / 关闭 / 点遮罩关闭 / 提交失败后自动重开
     4. data-confirm：危险操作二次确认
     5. data-copy：一键复制（密钥、URI）
     6. 轻提示自动消失
     7. data-tab：页内标签切换
     8. data-date-picker：文本框日期筛选 —— 点击弹原生日历，显示格式归页面控制
   ========================================================================== */

(function () {
    'use strict';

    /** 统一的事件绑定，元素不存在时静默跳过。 */
    function on(target, event, handler) {
        if (target) {
            target.addEventListener(event, handler);
        }
    }

    function all(selector, root) {
        return Array.prototype.slice.call((root || document).querySelectorAll(selector));
    }

    /**
     * 按 "#id" 或选择器取元素。
     *
     * 为什么不能只用 querySelector：本站在 ID 里嵌了业务标识符（Scope 名 `api:read`、
     * 角色名 `reports:viewer`、ClientId 等），而 id 里的 `:` 会被当成伪类分隔符 ——
     * `document.querySelector('#dlg-edit-api:read')` 直接抛 SyntaxError，
     * 结果就是"弹窗按钮点了没反应"。getElementById 接收任意 id 字面量、不做解析，
     * 因此这里统一回退到它。
     */
    function byIdOrSelector(selector) {
        if (!selector) {
            return null;
        }

        try {
            var found = document.querySelector(selector);
            if (found) {
                return found;
            }
        } catch (error) {
            /* 非法选择器：忽略，走下面的 id 查找 */
        }

        return selector.charAt(0) === '#' ? document.getElementById(selector.slice(1)) : null;
    }

    /* ---------------------------------------------------------- 1. 侧边栏 */

    function initSidebar() {
        var toggle = document.getElementById('ah-sidebar-toggle');
        var backdrop = document.querySelector('.ah-backdrop');

        function close() {
            document.body.classList.remove('ah-sidebar-open');
        }

        on(toggle, 'click', function () {
            document.body.classList.toggle('ah-sidebar-open');
        });
        on(backdrop, 'click', close);

        // 侧边栏里点了链接就收起，避免移动端遮挡内容
        all('.ah-nav-item').forEach(function (item) {
            on(item, 'click', close);
        });
    }

    /* -------------------------------------------------------- 2. 用户菜单 */

    function initDropdowns() {
        var triggers = all('[data-menu-toggle]');

        function closeAll(except) {
            all('.ah-menu').forEach(function (menu) {
                if (menu !== except) {
                    menu.hidden = true;
                }
            });
        }

        triggers.forEach(function (trigger) {
            var menu = document.getElementById(trigger.getAttribute('data-menu-toggle'));
            if (!menu) {
                return;
            }

            on(trigger, 'click', function (event) {
                event.stopPropagation();
                var willOpen = menu.hidden;
                closeAll(null);
                menu.hidden = !willOpen;
            });
        });

        on(document, 'click', function () { closeAll(null); });
        on(document, 'keydown', function (event) {
            if (event.key === 'Escape') {
                closeAll(null);
            }
        });
    }

    /* ------------------------------------------------------- 3. 模态对话框 */

    function initDialogs() {
        // 打开
        all('[data-dialog-open]').forEach(function (trigger) {
            on(trigger, 'click', function (event) {
                var dialog = byIdOrSelector(trigger.getAttribute('data-dialog-open'));
                if (dialog && typeof dialog.showModal === 'function') {
                    event.preventDefault();
                    dialog.showModal();
                }
            });
        });

        // 关闭
        all('[data-dialog-close]').forEach(function (trigger) {
            on(trigger, 'click', function (event) {
                var dialog = trigger.closest('dialog');
                if (dialog) {
                    event.preventDefault();
                    dialog.close();
                }
            });
        });

        // 点遮罩关闭：<dialog> 自身占满整个视口，点在内容之外的坐标即为遮罩
        all('dialog.ah-modal').forEach(function (dialog) {
            on(dialog, 'click', function (event) {
                if (event.target !== dialog) {
                    return;
                }

                var rect = dialog.getBoundingClientRect();
                var inside = event.clientX >= rect.left && event.clientX <= rect.right
                    && event.clientY >= rect.top && event.clientY <= rect.bottom;

                if (!inside) {
                    dialog.close();
                }
            });
        });

        // 服务端校验失败时重新打开对应的模态（表单值已原样回填）
        //
        // 契约：该属性的值只会是字符串 "true" 或 "false"（各页 *.cshtml 用显式三元写出下写字面量）。
        // 这里必须按**值**判断，不能按"属性是否存在"判断 —— 属性在每个弹窗上都存在。
        //
        // 曾经的故障：服务端那边写成 data-dialog-autoopen="@Model.AutoOpenCreate"，
        // Razor 把 bool 插值渲染成 "True"/"False" 字面量，于是这里按存在性判断时
        // 页面上**每一个**弹窗都被 showModal()，表现是"点菜单进入任一页面，全部弹窗一起弹出来"。
        // 现在两层都有防线：服务端写出小写字面量，这里也只认 "true"。
        all('dialog[data-dialog-autoopen]').forEach(function (dialog) {
            if (String(dialog.getAttribute('data-dialog-autoopen')).toLowerCase() !== 'true') {
                return;
            }

            if (typeof dialog.showModal === 'function') {
                dialog.showModal();
            }
        });
    }

    /* --------------------------------------------------- 4. 危险操作二次确认 */

    function initConfirm() {
        all('[data-confirm]').forEach(function (element) {
            var isForm = element.tagName === 'FORM';
            var eventName = isForm ? 'submit' : 'click';

            on(element, eventName, function (event) {
                if (!window.confirm(element.getAttribute('data-confirm'))) {
                    event.preventDefault();
                }
            });
        });
    }

    /* ---------------------------------------------------------- 5. 一键复制 */

    function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text);
        }

        // 非安全上下文（http）下 navigator.clipboard 不可用，退回 execCommand
        return new Promise(function (resolve) {
            var helper = document.createElement('textarea');
            helper.value = text;
            helper.setAttribute('readonly', 'readonly');
            helper.style.position = 'fixed';
            helper.style.opacity = '0';
            document.body.appendChild(helper);
            helper.select();
            try {
                document.execCommand('copy');
            } catch (error) {
                // 复制失败不阻断页面，用户仍可手工选中
            }
            document.body.removeChild(helper);
            resolve();
        });
    }

    function initCopy() {
        all('[data-copy]').forEach(function (trigger) {
            on(trigger, 'click', function (event) {
                event.preventDefault();

                var selector = trigger.getAttribute('data-copy');
                var source = byIdOrSelector(selector);
                var text = source ? source.textContent.trim() : trigger.getAttribute('data-copy-value');

                if (!text) {
                    return;
                }

                copyText(text).then(function () {
                    var original = trigger.getAttribute('data-copy-label') || trigger.textContent;
                    trigger.textContent = '已复制';
                    window.setTimeout(function () {
                        trigger.textContent = original;
                    }, 1400);
                });
            });
        });
    }

    /* ------------------------------------------------------- 6. 轻提示消失 */

    function initToasts() {
        all('.ah-toast[data-autohide]').forEach(function (toast) {
            var delay = parseInt(toast.getAttribute('data-autohide'), 10) || 4000;
            window.setTimeout(function () {
                toast.style.transition = 'opacity .25s ease';
                toast.style.opacity = '0';
                window.setTimeout(function () {
                    if (toast.parentNode) {
                        toast.parentNode.removeChild(toast);
                    }
                }, 260);
            }, delay);
        });
    }

    /* --------------------------------------------------------- 7. 页内标签 */

    function initTabs() {
        all('[data-tab]').forEach(function (tab) {
            on(tab, 'click', function (event) {
                event.preventDefault();

                var target = tab.getAttribute('data-tab');
                var scope = tab.closest('[data-tab-scope]') || document;

                all('[data-tab]', scope).forEach(function (other) {
                    var active = other === tab;
                    other.classList.toggle('is-active', active);

                    // 只同步声明了 role="tab" 的元素：没有该 role 时写 aria-selected 是无意义的
                    if (other.getAttribute('role') === 'tab') {
                        other.setAttribute('aria-selected', active ? 'true' : 'false');
                        other.setAttribute('tabindex', active ? '0' : '-1');
                    }
                });
                all('[data-tab-panel]', scope).forEach(function (panel) {
                    panel.hidden = panel.getAttribute('data-tab-panel') !== target;
                });

                // 让刷新后仍停留在同一标签
                if (window.history && window.history.replaceState) {
                    window.history.replaceState(null, '', '#' + target);
                }
            });
        });

        // 支持从锚点直接进入某个标签（例如 /admin/profile#mfa）
        if (window.location.hash) {
            var initial = document.querySelector('[data-tab="' + window.location.hash.slice(1) + '"]');
            if (initial) {
                initial.click();
            }
        }
    }

    /* ------------------------------------------------- 8. 文本框日期筛选 */

    /**
     * 把用户手输的日期归一成 yyyy-MM-dd（原生日历只认这个格式）。
     * 兼容连字符 / 斜杠 / 点三种分隔符，月日允许 1~2 位；解析不了返回空串。
     */
    function normalizeDateInput(text) {
        var match = /^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$/.exec(String(text).trim());
        if (!match) {
            return '';
        }

        var year = match[1];
        var month = Number(match[2]);
        var day = Number(match[3]);
        if (month < 1 || month > 12 || day < 1 || day > 31) {
            return '';
        }

        return year + '-' + ('0' + month).slice(-2) + '-' + ('0' + day).slice(-2);
    }

    function initDatePickers() {
        all('[data-date-picker]').forEach(function (input) {
            if (input.getAttribute('data-date-picker-bound') === '1') {
                return;
            }
            input.setAttribute('data-date-picker-bound', '1');

            // 原生日历必须挂在一个 <input type="date"> 上才能弹。做一个 1px 的
            // 透明代理放在旁边：display:none 的元素 showPicker() 会抛错，
            // 所以保持渲染、只让它不可见也不可交互（tabIndex=-1 不进 tab 序，
            // 不设 name 因此不会随表单提交）。
            var proxy = document.createElement('input');
            proxy.type = 'date';
            proxy.className = 'ah-date-proxy';
            proxy.tabIndex = -1;
            proxy.setAttribute('aria-hidden', 'true');
            input.insertAdjacentElement('afterend', proxy);

            on(proxy, 'change', function () {
                input.value = proxy.value;
                input.dispatchEvent(new Event('change', { bubbles: true }));
            });

            on(input, 'click', function () {
                // 打开前把文本框里已有的值同步给日历，用户改起来有起点
                proxy.value = normalizeDateInput(input.value);

                try {
                    if (typeof proxy.showPicker === 'function') {
                        proxy.showPicker();
                    }
                } catch (error) {
                    // showPicker 需要"用户激活"且受浏览器策略限制，失败就静默 ——
                    // 文本框本身仍可手输（后端兼容 yyyy-MM-dd / yyyy/M/d 等写法）
                }
            });
        });
    }

    /* ------------------------------------------------------------- 初始化 */

    function init() {
        initSidebar();
        initDropdowns();
        initDialogs();
        initConfirm();
        initCopy();
        initToasts();
        initTabs();
        initDatePickers();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
