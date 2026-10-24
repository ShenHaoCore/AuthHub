/* ==========================================================================
   AuthHub 管理后台交互脚本（原生 JS，无框架、无外部依赖）

   为什么必须有这个文件：
     本站 CSP 是 script-src 'self'，行内 <script> 与 on* 事件属性都会被浏览器
     静默拦截。因此所有交互只能通过这里注册的 addEventListener 完成。

   覆盖的交互（均为渐进增强，页面在无 JS 时仍可读、表单仍可提交）：
     1. 移动端侧边栏开合
     2. 顶栏用户菜单
     3. <dialog> 模态：打开 / 关闭 / 点遮罩关闭 / 提交失败后自动重开
     4. data-confirm：危险操作二次确认（自绘确认弹窗，替代原生 confirm()）
     5. data-copy：一键复制（密钥、URI）
     6. 轻提示：服务端 TempData 渲染的 + 局部刷新响应头带出的
     7. data-tab：页内标签切换
     8. data-date-picker：文本框日期筛选 —— 点击弹自绘日历
     9. data-ajax / data-ajax-link：局部刷新层 —— 表单提交与链接点击改为
        fetch + 替换 #ah-content，页面不再整页重载（滚动与侧边栏状态得以保留）。
        服务端契约：请求带 X-AH-Partial: 1 头时，页面把 Layout 置空，
        Page() 直接渲染出内容片段；X-AH-Toast 头（URI 编码的 {t,m} JSON）
        由本文件转成轻提示。

   事件绑定策略：能委托的全部委托到 document —— #ah-content 被局部替换后，
   新元素自动被现有监听覆盖，无需任何重绑；只有永远不被替换的元素
   （侧边栏、顶栏菜单）保留元素级绑定。
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

    /**
     * 打开 / 关闭 / 点遮罩关闭全部委托到 document：
     * 局部刷新会整块替换 #ah-content（弹窗标记都在里面），元素级绑定会随之丢失。
     */
    function initDialogs() {
        on(document, 'click', function (event) {
            var opener = event.target.closest('[data-dialog-open]');
            if (opener) {
                var dialog = byIdOrSelector(opener.getAttribute('data-dialog-open'));
                if (dialog && typeof dialog.showModal === 'function') {
                    event.preventDefault();
                    dialog.showModal();
                }
                return;
            }

            var closer = event.target.closest('[data-dialog-close]');
            if (closer) {
                var host = closer.closest('dialog');
                if (host) {
                    event.preventDefault();
                    host.close();
                }
                return;
            }

            // 点遮罩关闭：<dialog> 自身占满整个视口，点在内容之外的坐标即为遮罩
            if (event.target instanceof HTMLDialogElement && event.target.classList.contains('ah-modal')) {
                var rect = event.target.getBoundingClientRect();
                var inside = event.clientX >= rect.left && event.clientX <= rect.right
                    && event.clientY >= rect.top && event.clientY <= rect.bottom;

                if (!inside) {
                    event.target.close();
                }
            }
        });
    }

    /**
     * 打开页面上标记了 data-dialog-autoopen="true" 的弹窗。
     *
     * 契约：该属性的值只会是字符串 "true" 或 "false"（各页 *.cshtml 用显式三元写出下写字面量）。
     * 这里必须按**值**判断，不能按"属性是否存在"判断 —— 属性在每个弹窗上都存在。
     *
     * 曾经的故障：服务端那边写成 data-dialog-autoopen="@Model.AutoOpenCreate"，
     * Razor 把 bool 插值渲染成 "True"/"False" 字面量，于是这里按存在性判断时
     * 页面上**每一个**弹窗都被 showModal()，表现是"点菜单进入任一页面，全部弹窗一起弹出来"。
     * 现在两层都有防线：服务端写出小写字面量，这里也只认 "true"。
     */
    function scanAutoOpenDialogs() {
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

    // 与 AdminNav.Icons.Alert 同源的图标路径（CSP 禁内联脚本，这里只能硬编码字面量）
    var ICON_PATHS = {
        alert: 'M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0zM12 9v4M12 17h.01',
        check: 'M20 6L9 17l-5-5'
    };

    function iconSvg(path, size) {
        return '<svg width="' + size + '" height="' + size + '" viewBox="0 0 24 24" fill="none"'
            + ' stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
            + '<path d="' + path + '"/></svg>';
    }

    var confirmEl = null;
    var confirmResolve = null;

    function ensureConfirmDialog() {
        if (confirmEl) {
            return;
        }

        confirmEl = document.createElement('dialog');
        confirmEl.className = 'ah-modal ah-modal-confirm';
        confirmEl.setAttribute('aria-labelledby', 'ah-confirm-title');
        confirmEl.innerHTML =
            '<div class="ah-modal-head"><h2 id="ah-confirm-title">确认操作</h2></div>'
            + '<div class="ah-modal-body ah-confirm-body">'
            + '<span class="ah-confirm-icon" aria-hidden="true">' + iconSvg(ICON_PATHS.alert, 19) + '</span>'
            + '<p class="ah-confirm-text"></p>'
            + '</div>'
            + '<div class="ah-modal-foot">'
            + '<div class="ah-modal-foot-end">'
            + '<button type="button" class="ah-btn" data-ah-confirm-cancel>取消</button>'
            + '<button type="button" class="ah-btn ah-btn--danger" data-ah-confirm-ok>确认</button>'
            + '</div>'
            + '</div>';
        document.body.appendChild(confirmEl);

        on(confirmEl, 'click', function (event) {
            if (event.target.closest('[data-ah-confirm-cancel]')) {
                confirmEl.close('');
            } else if (event.target.closest('[data-ah-confirm-ok]')) {
                confirmEl.close('ok');
            }
        });

        on(confirmEl, 'close', function () {
            var resolve = confirmResolve;
            confirmResolve = null;
            if (resolve) {
                resolve(confirmEl.returnValue === 'ok');
            }
        });

        on(confirmEl, 'cancel', function (event) {
            // Esc 等取消也要走 close 流程,保证 Promise 一定被 resolve
            event.preventDefault();
            confirmEl.close('');
        });
    }

    /**
     * 样式化确认弹窗。替代 window.confirm():原生弹窗与整站设计语言脱节,
     * 且在各浏览器里样式不受控。焦点落在「取消」上 —— 危险操作的安全默认。
     */
    function confirmDialog(message) {
        ensureConfirmDialog();

        // 防御:上一个确认还没关闭又来一个(理论上不会发生),先按取消结算
        if (confirmResolve) {
            confirmResolve(false);
            confirmResolve = null;
        }

        return new Promise(function (resolve) {
            confirmEl.querySelector('.ah-confirm-text').textContent = message;
            confirmResolve = resolve;
            confirmEl.showModal();
            confirmEl.querySelector('[data-ah-confirm-cancel]').focus();
        });
    }

    /* ------------------------------------------------------- 5. 一键复制 */

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
        on(document, 'click', function (event) {
            var trigger = event.target.closest('[data-copy]');
            if (!trigger) {
                return;
            }

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
    }

    /* ---------------------------------------------------------- 6. 轻提示 */

    function autohideToast(toast, delay) {
        window.setTimeout(function () {
            toast.style.transition = 'opacity .25s ease';
            toast.style.opacity = '0';
            window.setTimeout(function () {
                if (toast.parentNode) {
                    toast.parentNode.removeChild(toast);
                }
            }, 260);
        }, delay);
    }

    /**
     * 客户端生成轻提示。局部刷新动作的反馈不再走「302 + TempData + 整页」,
     * 服务端把文案放在 X-AH-Toast 响应头里,这里渲染成与布局页同结构的 toast。
     */
    function showToast(type, message) {
        var host = document.querySelector('.ah-toast-host');
        if (!host) {
            return;
        }

        var toast = document.createElement('div');
        toast.className = 'ah-toast ah-toast--' + (type === 'danger' ? 'danger' : 'success');
        toast.setAttribute('role', type === 'danger' ? 'alert' : 'status');
        toast.innerHTML = iconSvg(type === 'danger' ? ICON_PATHS.alert : ICON_PATHS.check, 16)
            + '<span></span>';
        // 文案只走 textContent,即使来源异常也不会注入标记
        toast.lastElementChild.textContent = message;
        host.appendChild(toast);

        // 与布局页渲染的成功提示同参数;danger 不自动消失,必须人工看到
        if (type !== 'danger') {
            autohideToast(toast, 4500);
        }
    }

    /** 扫描服务端(TempData)渲染的 toast —— 仅整页加载时存在。 */
    function initToasts() {
        all('.ah-toast[data-autohide]').forEach(function (toast) {
            autohideToast(toast, parseInt(toast.getAttribute('data-autohide'), 10) || 4000);
        });
    }

    /* --------------------------------------------------------- 7. 页内标签 */

    function initTabs() {
        /** 把某个标签设为激活：同步兄弟标签的 class / aria，并只显示它对应的面板。 */
        function activate(tab, scope) {
            var target = tab.getAttribute('data-tab');

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
        }

        /** 激活 hash 指向的标签。hash 为空、指向不存在的标签、或不是合法选择器时，什么都不做。 */
        function activateFromHash() {
            var hash = window.location.hash;
            if (hash.length < 2) {
                return;
            }

            var byHash;
            try {
                byHash = document.querySelector('[data-tab="' + hash.slice(1) + '"]');
            } catch (error) {
                return;
            }

            if (byHash) {
                activate(byHash, byHash.closest('[data-tab-scope]') || document);
            }
        }

        all('[data-tab]').forEach(function (tab) {
            on(tab, 'click', function (event) {
                event.preventDefault();
                activate(tab, tab.closest('[data-tab-scope]') || document);

                // 让刷新后仍停留在同一标签
                if (window.history && window.history.replaceState) {
                    window.history.replaceState(null, '', '#' + tab.getAttribute('data-tab'));
                }
            });
        });

        // 支持从锚点直接进入某个标签（例如从别的页点「修改密码」跳到 /admin/profile#password）
        activateFromHash();

        // 但已经停在本页时点同一个链接，属于"同文档锚点导航"：浏览器只改 hash、
        // 不会重新加载页面，上面的初始化也就不会重跑 —— 少了这个监听，
        // 用户看到的就是「点了没反应」。
        on(window, 'hashchange', activateFromHash);
    }

    /* ------------------------------------------------- 8. 文本框日期筛选 */

    /**
     * 把用户手输的日期归一成 yyyy-MM-dd（后端筛选参数只认这个格式）。
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

        return formatYmd(year, month, day);
    }

    function formatYmd(year, month, day) {
        return year + '-' + ('0' + month).slice(-2) + '-' + ('0' + day).slice(-2);
    }

    /* 为什么自绘日历而不是原生气泡：
       原生 <input type="date"> 的日历是浏览器画的独立图层，页面 CSS 够不着 ——
       只能跟随 color-scheme 在明/暗两档里二选一（白弹层盖白表格看不清，
       深色弹层又与浅色后台风格不搭）。自绘面板用设计令牌，风格与后台一致。 */

    var DATE_WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日'];

    var datepickerEl = null;
    var datepickerState = null; // { input, year, month(0-11), selected }

    function todayYmd() {
        var now = new Date();
        return formatYmd(now.getFullYear(), now.getMonth() + 1, now.getDate());
    }

    function ensureDatepicker() {
        if (datepickerEl) {
            return;
        }

        datepickerEl = document.createElement('div');
        datepickerEl.className = 'ah-datepicker';
        datepickerEl.setAttribute('role', 'dialog');
        datepickerEl.setAttribute('aria-label', '选择日期');
        document.body.appendChild(datepickerEl);

        // 面板内部的点击不冒泡到 document（document 上挂着"点外面就关"的监听）
        on(datepickerEl, 'click', function (event) {
            var button = event.target.closest('button');
            if (!button || !datepickerState) {
                return;
            }

            if (button.hasAttribute('data-nav')) {
                datepickerState.month += Number(button.getAttribute('data-nav'));
                normalizeViewMonth();
                renderDatepicker();
            } else if (button.hasAttribute('data-date')) {
                commitDate(button.getAttribute('data-date'));
            } else if (button.getAttribute('data-act') === 'clear') {
                commitDate('');
            } else if (button.getAttribute('data-act') === 'today') {
                commitDate(todayYmd());
            }
            event.stopPropagation();
        });

        on(document, 'click', function (event) {
            // 触发输入框本身的点击要「重开」而不是「关」；面板内部已由 stopPropagation 拦下
            if (event.target.closest && event.target.closest('[data-date-picker]')) {
                return;
            }
            closeDatepicker();
        });
        on(document, 'keydown', function (event) {
            if (event.key === 'Escape') {
                closeDatepicker();
            }
        });

        // 弹层是 fixed 定位：页面一滚动锚点就飘了，直接关掉最省事。
        // 捕获阶段监听（on 助手不支持 options），任何容器内滚动都能收到。
        window.addEventListener('scroll', closeDatepicker, true);
        on(window, 'resize', closeDatepicker);
    }

    function normalizeViewMonth() {
        var total = datepickerState.year * 12 + datepickerState.month;
        datepickerState.year = Math.floor(total / 12);
        datepickerState.month = total % 12;
        if (datepickerState.month < 0) {
            datepickerState.year -= 1;
            datepickerState.month += 12;
        }
    }

    function renderDatepicker() {
        var state = datepickerState;
        // 周一作为第一列：getDay() 周日=0 … 周六=6，(getDay()+6)%7 即距周一的天数
        var lead = (new Date(state.year, state.month, 1).getDay() + 6) % 7;
        var daysInMonth = new Date(state.year, state.month + 1, 0).getDate();
        var today = todayYmd();

        var html = '<div class="ah-datepicker-head">' +
            '<button type="button" class="ah-datepicker-nav" data-nav="-1" aria-label="上个月">‹</button>' +
            '<span class="ah-datepicker-title">' + state.year + ' 年 ' + (state.month + 1) + ' 月</span>' +
            '<button type="button" class="ah-datepicker-nav" data-nav="1" aria-label="下个月">›</button>' +
            '</div>' +
            '<div class="ah-datepicker-grid">';
        DATE_WEEKDAYS.forEach(function (name) {
            html += '<span class="ah-datepicker-wd">' + name + '</span>';
        });
        for (var i = 0; i < lead; i++) {
            html += '<span></span>';
        }
        for (var day = 1; day <= daysInMonth; day++) {
            var value = formatYmd(state.year, state.month + 1, day);
            var cls = 'ah-datepicker-day';
            if (value === today) {
                cls += ' is-today';
            }
            if (value === state.selected) {
                cls += ' is-selected';
            }
            html += '<button type="button" class="' + cls + '" data-date="' + value + '">' + day + '</button>';
        }
        html += '</div>' +
            '<div class="ah-datepicker-foot">' +
            '<button type="button" class="ah-datepicker-clear" data-act="clear">清除</button>' +
            '<button type="button" class="ah-datepicker-today" data-act="today">今天</button>' +
            '</div>';

        datepickerEl.innerHTML = html;
    }

    function positionDatepicker() {
        var rect = datepickerState.input.getBoundingClientRect();
        var width = datepickerEl.offsetWidth;
        var height = datepickerEl.offsetHeight;

        var top = rect.bottom + 6;
        if (top + height > window.innerHeight - 8) {
            top = Math.max(8, rect.top - height - 6); // 下方放不下就翻到输入框上方
        }
        var left = Math.min(rect.left, window.innerWidth - width - 8);

        datepickerEl.style.top = top + 'px';
        datepickerEl.style.left = Math.max(8, left) + 'px';
    }

    function openDatepicker(input) {
        ensureDatepicker();

        // 打开前把文本框里已有的值同步给日历，用户改起来有起点
        var parsed = normalizeDateInput(input.value);
        var parts = parsed ? parsed.split('-') : null;
        var now = new Date();
        datepickerState = {
            input: input,
            selected: parsed,
            year: parts ? Number(parts[0]) : now.getFullYear(),
            month: parts ? Number(parts[1]) - 1 : now.getMonth()
        };

        renderDatepicker();
        positionDatepicker();
        datepickerEl.classList.add('is-open');
    }

    function closeDatepicker() {
        if (datepickerEl) {
            datepickerEl.classList.remove('is-open');
        }
    }

    function commitDate(value) {
        var input = datepickerState.input;
        input.value = value;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        closeDatepicker();
    }

    function initDatePickers() {
        // 委托版:筛选工具栏随 #ah-content 一起被局部替换,元素级绑定会丢
        on(document, 'click', function (event) {
            var input = event.target.closest('[data-date-picker]');
            if (input) {
                openDatepicker(input);
            }
        });
    }

    /* ------------------------------------------------- 9. 局部刷新层 */

    var PARTIAL_HEADER = { 'X-AH-Partial': '1' };

    /** 解析服务端的 X-AH-Toast 响应头（URI 编码的 {"t":..,"m":..}）。 */
    function toastFromResponse(response) {
        var header = response.headers.get('X-AH-Toast');
        if (!header) {
            return;
        }

        try {
            var detail = JSON.parse(decodeURIComponent(header));
            showToast(detail.t, detail.m);
        } catch (error) {
            /* 头格式异常时静默丢弃:提示是增强,不是主流程 */
        }
    }

    /**
     * 把服务端返回的内容片段应用进容器。
     *
     * originatingDialog:这次提交来自哪个弹窗 —— 无论成败都先关掉它;
     * 失败时服务端会把出错的弹窗标记成 data-dialog-autoopen="true",
     * 替换完成后 scanAutoOpenDialogs() 会带着错误信息重开它。
     */
    function applyPartial(response, text, container, originatingDialog, pushUrl) {
        var fragment = text.replace(/^\s+/, '');

        // 服务端返回了整页文档(局部请求头被中间件剥掉之类的异常),退回整页导航
        if (/^<!doctype/i.test(fragment) || /^<html/i.test(fragment)) {
            if (pushUrl) {
                window.location.assign(pushUrl);
            } else {
                window.location.reload();
            }
            return;
        }

        toastFromResponse(response);

        if (originatingDialog && originatingDialog.open) {
            originatingDialog.close();
        }

        container.innerHTML = text;

        if (pushUrl) {
            // GET(筛选/翻页)同步地址栏:刷新与分享都能回到同一视图
            window.history.pushState(null, '', pushUrl);
            scrollToContent(container);
        }

        scanAutoOpenDialogs();
    }

    /** 翻页 / 筛选后回到内容区顶部;行内操作不滚动(位置本来就没变)。 */
    function scrollToContent(container) {
        var top = container.getBoundingClientRect().top + window.pageYOffset - 70;
        if (window.pageYOffset > top) {
            window.scrollTo(0, top);
        }
    }

    function ajaxFetch(url, options) {
        return fetch(url, options).then(function (response) {
            return response.text().then(function (text) {
                return { response: response, text: text };
            });
        });
    }

    function submitAjaxForm(form) {
        var container = byIdOrSelector(form.getAttribute('data-ajax'));
        if (!container) {
            // 容器不存在(异常标记):退回原生提交,宁可整页刷新也不吞提交
            form.removeAttribute('data-ajax');
            form.submit();
            return;
        }

        var method = (form.getAttribute('method') || 'get').toUpperCase();
        var action = form.getAttribute('action') || window.location.pathname + window.location.search;
        var isGet = method === 'GET';
        var url = action;
        var options = { method: method, headers: PARTIAL_HEADER, credentials: 'same-origin' };

        if (isGet) {
            // FormData 只收启用中的控件,勾选态与选中项都如实序列化
            var query = new URLSearchParams(new FormData(form)).toString();
            if (query) {
                url += (url.indexOf('?') >= 0 ? '&' : '?') + query;
            }
        } else {
            options.body = new FormData(form);
        }

        // 先捕获表单数据再置忙:disabled 的按钮不会进 FormData
        var button = form.querySelector('button[type="submit"], button:not([type])');
        if (button) {
            button.disabled = true;
        }

        ajaxFetch(url, options).then(function (result) {
            if (!result.response.ok) {
                showToast('danger', '请求未完成（HTTP ' + result.response.status + '），请重试。');
                if (button) {
                    button.disabled = false;
                }
                return;
            }

            applyPartial(result.response, result.text, container, form.closest('dialog'), isGet ? url : null);
        }).catch(function () {
            showToast('danger', '网络异常，操作未完成，请重试。');
            if (button) {
                button.disabled = false;
            }
        });
    }

    function fetchPartialUrl(url, containerSelector) {
        var container = byIdOrSelector(containerSelector);
        if (!container) {
            window.location.assign(url);
            return;
        }

        ajaxFetch(url, { headers: PARTIAL_HEADER, credentials: 'same-origin' }).then(function (result) {
            if (!result.response.ok) {
                window.location.assign(url);
                return;
            }

            applyPartial(result.response, result.text, container, null, url);
        }).catch(function () {
            window.location.assign(url);
        });
    }

    /**
     * 表单提交的统一入口:确认弹窗与局部刷新在同一处协调,
     * 避免两个独立监听器对同一次 submit 各自 preventDefault 互相打架。
     */
    function initSubmitPipeline() {
        on(document, 'submit', function (event) {
            var form = event.target;
            if (!(form instanceof HTMLFormElement)) {
                return;
            }

            var needsConfirm = form.hasAttribute('data-confirm');
            var isAjax = form.hasAttribute('data-ajax');

            // 二次放行(见下):确认已通过,该干嘛干嘛
            if (form.getAttribute('data-ah-confirmed') === '1') {
                form.removeAttribute('data-ah-confirmed');
                if (isAjax) {
                    event.preventDefault();
                    submitAjaxForm(form);
                }
                return;
            }

            if (!needsConfirm && !isAjax) {
                return;
            }

            event.preventDefault();

            function proceed() {
                if (isAjax) {
                    submitAjaxForm(form);
                } else {
                    // requestSubmit 会重跑校验并再次触发 submit 事件;
                    // 上面的 data-ah-confirmed 标记让第二次进监听器时直接放行
                    form.setAttribute('data-ah-confirmed', '1');
                    form.requestSubmit();
                }
            }

            if (needsConfirm) {
                confirmDialog(form.getAttribute('data-confirm')).then(function (confirmed) {
                    if (confirmed) {
                        proceed();
                    }
                });
            } else {
                proceed();
            }
        });
    }

    function initAjaxLinks() {
        on(document, 'click', function (event) {
            var link = event.target.closest('a[data-ajax-link]');
            if (!link) {
                return;
            }

            // 修饰键点击是用户在要"新标签打开",交给浏览器
            if (event.defaultPrevented || event.button !== 0
                || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
                return;
            }

            var url = link.getAttribute('href');
            if (!url || (link.origin && link.origin !== window.location.origin)) {
                return;
            }

            event.preventDefault();
            fetchPartialUrl(url, link.getAttribute('data-ajax-link'));
        });
    }

    /* ------------------------------------------------------------- 初始化 */

    function init() {
        initSidebar();
        initDropdowns();
        initDialogs();
        initSubmitPipeline();
        initCopy();
        initToasts();
        initTabs();
        initDatePickers();
        initAjaxLinks();
        scanAutoOpenDialogs();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
