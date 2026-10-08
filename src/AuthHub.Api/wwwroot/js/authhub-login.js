/* ==========================================================================
   登录页交互（页签切换）
   --------------------------------------------------------------------------
   约束：本站 CSP 是 script-src 'self'，行内 <script> 与 on* 事件属性一律拒绝，
   所有交互只能通过本文件注册的事件监听器完成。

   设计为渐进增强：无 JS 时两个面板上下展开都可用；有 JS 时只显示当前页签面板，
   点击页签做无刷新切换。
   ========================================================================== */

(function () {
    "use strict";

    const tabBar = document.querySelector("[role='tablist']");
    if (!tabBar) return;

    const tabs = tabBar.querySelectorAll("[role='tab']");
    const panels = document.querySelectorAll("[data-login-panel]");
    if (tabs.length === 0 || panels.length === 0) return;

    function activate(targetName) {
        tabs.forEach(tab => {
            const isActive = tab.dataset.loginTab === targetName;
            tab.classList.toggle("is-active", isActive);
            tab.setAttribute("aria-selected", String(isActive));
        });
        // 用 hidden 属性折叠面板而非 CSS 类：初始 HTML 不带 hidden（无 JS 时全展开），
        // 折叠状态完全由脚本生命周期管理，降级路径不依赖任何样式约定。
        panels.forEach(panel => {
            panel.hidden = panel.dataset.loginPanel !== targetName;
        });
    }

    tabBar.addEventListener("click", function (e) {
        const tab = e.target.closest("[role='tab']");
        if (!tab) return;
        activate(tab.dataset.loginTab);
    });

    // 键盘支持：左右箭头切换页签
    tabBar.addEventListener("keydown", function (e) {
        const current = tabBar.querySelector("[role='tab'].is-active");
        if (!current) return;
        const list = Array.from(tabs);
        let idx = list.indexOf(current);

        if (e.key === "ArrowRight") {
            idx = (idx + 1) % list.length;
            e.preventDefault();
            activate(list[idx].dataset.loginTab);
            list[idx].focus();
        } else if (e.key === "ArrowLeft") {
            idx = (idx - 1 + list.length) % list.length;
            e.preventDefault();
            activate(list[idx].dataset.loginTab);
            list[idx].focus();
        }
    });

    // 初始折叠非活跃面板（仅当 JS 执行后才生效：无 JS 时所有面板都可见）
    const activeTab = tabBar.querySelector("[role='tab'].is-active");
    if (activeTab) {
        activate(activeTab.dataset.loginTab);
    }
})();
