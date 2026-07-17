// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

/* Gelo client — injects a "Recommended For You" rail plus categorized recommendation shelves into
 * the jellyfin-web home, and a "Recommended" rail onto Movie/Series detail pages. Reads the user's
 * ApiClient (auto-authenticated). DOM-tolerant:
 * it locates the home by the stable ".verticalSection" sections (not by chunk internals) and renders
 * its own upgrade-resilient cards, so it survives jellyfin-web point releases.
 *
 * React-coexistence (10.11 home is React-rendered): we never fight the reconciler. The shelves live in
 * our own .gelo-host node, which the MutationObserver is told to ignore (so our writes never re-trigger
 * us). We keep a reference to the built host and, if React unmounts the page beneath it, simply move the
 * SAME element back into the (re-)mounted container — instant, no refetch, no loading flash, no reorder.
 * Duplicate/orphaned hosts are deduped each tick so a container swap can never stack shelves.
 *
 * Config is spliced into the placeholder below by the server (WebAssetController.GetGeloJs). The
 * placeholder is wrapped in a JS object literal, so an un-replaced serve is still valid (empty config)
 * and the defaults below take over.
 */
(function () {
    "use strict";
    if (window.__geloLoaded) return;
    window.__geloLoaded = true;

    var CFG = {/*GELO_CONFIG*/};
    var BASE = CFG.base || "/CustomRecommendations";
    var MAX = (CFG.shelfCount | 0) || 4;
    var POS = (CFG.position === "top" || CFG.position === "bottom") ? CFG.position : "afterFirst";
    var DETAIL_LIMIT = (CFG.detailLimit | 0) || 12;
    var FORYOU_TITLE = (CFG.forYouTitle || "Recommended For You");
    var FORYOU_LIMIT = (CFG.forYouLimit | 0) || 24;

    var HOME_HOST_SEL = 'gelo-host[data-gelo="home"]';
    var DETAIL_HOST_SEL = 'gelo-host[data-gelo="detail"]';

    // Cached built hosts — re-attached (not rebuilt) on eviction, so React unmount/remount never flickers.
    var homeHostEl = null;
    var detailHostEl = null;
    var detailCacheId = null;

    function api() { return window.ApiClient; }
    function uid() { var a = api(); return a && a.getCurrentUserId ? a.getCurrentUserId() : null; }
    function serverId() {
        try { var a = api(); return (a && typeof a.serverId === "function") ? (a.serverId() || "") : ""; }
        catch (e) { return ""; }
    }
    function ready() { return !!(api() && uid()); }

    function geloGet(path) { return api().ajax({ url: BASE + path, type: "GET", dataType: "json" }); }
    function nativeGet(path) { var a = api(); return a.ajax({ url: a.getUrl(path), type: "GET", dataType: "json" }); }

    // Hydrate ranked IDs into full card-ready BaseItemDtos via the native Items endpoint.
    function hydrate(ids) {
        if (!ids || !ids.length) return Promise.resolve([]);
        var q = "Ids=" + ids.slice(0, 30).join(",") +
            "&Fields=PrimaryImageAspectRatio,ImageTags,UserData" +
            "&EnableImageTypes=Primary,Backdrop,Thumb";
        return nativeGet("Users/" + uid() + "/Items?" + q).then(function (r) { return (r && r.Items) || []; });
    }

    function esc(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function imgFor(it) {
        var a = api();
        var tags = it.ImageTags || {};
        var tag = tags.Primary || it.PrimaryImageTag;
        if (tag) return a.getUrl("Items/" + it.Id + "/Images/Primary", { tag: tag, quality: 90 });
        if (it.BackdropImageTags && it.BackdropImageTags[0])
            return a.getUrl("Items/" + it.Id + "/Images/Backdrop/0", { tag: it.BackdropImageTags[0], quality: 90 });
        if (tags.Thumb) return a.getUrl("Items/" + it.Id + "/Images/Thumb", { tag: tags.Thumb, quality: 90 });
        return null;
    }

    // ── Native card rendering ──────────────────────────────────────────
    // The themeable path: reach jellyfin-web's OWN cardBuilder (a webpack module) and call its
    // getCardsHtml() — the exact function the native home shelves use. That yields pixel-native cards:
    // the per-screen width it computes inline, the full native class tree, hover/play overlays,
    // watched + progress indicators, and full theme support. We resolve the module by scanning webpack
    // factory source (content-based, so it survives chunk-id shifts across builds). If the runtime
    // can't be reached we fall back to a minimal native-class card (cardHtml below).
    var _cardBuilder = null, _cbTried = false;
    function getCardBuilder() {
        if (_cbTried) return _cardBuilder;
        _cbTried = true;
        try {
            var chunks = window.webpackChunk;
            if (!chunks) return null;
            var req = null;
            // Webpack 5 push-trick: the 3rd arg receives __webpack_require__.
            chunks.push([["__gelo_cb__"], {}, function (r) { req = r; }]);
            if (!req || !req.m) return null;
            var id = null, mods = req.m;
            for (var k in mods) {
                var src = String(mods[k]);
                if (src.indexOf("getCardsHtml") >= 0 && src.indexOf("cardPadder") >= 0) { id = k; break; }
            }
            if (id == null) return null;
            var mod = req(id);
            _cardBuilder = (mod && typeof mod.getCardsHtml === "function") ? mod
                : (mod && mod.default && typeof mod.default.getCardsHtml === "function") ? mod.default
                : null;
        } catch (e) { _cardBuilder = null; }
        return _cardBuilder;
    }

    // cardBuilder always emits lazy image containers (class "lazy" + data-src); the native caller
    // hydrates them via imageLoader. We skip that dependency by setting the background-image directly.
    function hydrateLazy(root) {
        var nodes = root.querySelectorAll("[data-src]");
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i], src = n.getAttribute("data-src");
            if (src) n.style.backgroundImage = "url('" + src + "')";
            n.classList.remove("lazy");
        }
    }

    // Render ranked items as native cards (preferred) or manual fallback cards.
    function cardsHtml(items) {
        var cb = getCardBuilder();
        if (cb) {
            try {
                var html = cb.getCardsHtml({
                    items: items,
                    shape: "overflowPortrait",
                    showTitle: true,
                    showYear: true,
                    centerText: true,
                    overlayPlayButton: true,
                    lines: 2,
                    context: "home"
                });
                if (html) return html;
            } catch (e) { /* fall through to manual */ }
        }
        return items.map(cardHtml).join("");
    }

    function detailHref(id) { return "#/details?id=" + id + (serverId() ? ("&serverId=" + serverId()) : ""); }

    // FALLBACK card — only used if the native cardBuilder can't be reached. Reuses the native class
    // tree + the native overflowPortraitCard width class (vw-based, responsive, themeable) so it still
    // matches builtin cards as closely as possible without the real cardBuilder.
    function cardHtml(it) {
        var url = imgFor(it);
        var name = esc(it.Name);
        var ud = it.UserData || {};
        var year = it.ProductionYear
            ? '<div class="cardText cardTextCentered cardText-secondary">' + esc(it.ProductionYear) + "</div>"
            : "";
        var played = ud.Played
            ? '<i class="gelo-played material-icons" title="Watched">check_circle</i>'
            : "";
        var prog = (ud.PlayedPercentage != null && ud.PlayedPercentage > 0 && ud.PlayedPercentage < 100)
            ? '<div class="gelo-progress"><span style="width:' + Math.round(ud.PlayedPercentage) + '%"></span></div>'
            : "";
        var img = url
            ? '<div class="cardImageContainer coveredImage" style="background-image:url(\'' + url + '\')">' + played + prog + "</div>"
            : '<div class="cardImageContainer coveredImage" style="background-color:#1a1a1e"><div class="gelo-card-ph">' + name + "</div></div>";
        return '<a class="card overflowPortraitCard" data-id="' + it.Id + '" href="' + detailHref(it.Id) + '">' +
            '<div class="cardBox visualCardBox"><div class="cardScalable">' +
            '<div class="cardPadder cardPadder-portrait"></div>' +
            '<div class="cardContent">' + img + "</div>" +
            "</div></div>" +
            '<div class="cardFooter">' +
            '<div class="cardText cardTextCentered cardText-first">' + name + "</div>" +
            year +
            "</div></a>";
    }

    // Native shelf recipe (from homesections/sections/recentlyAdded.ts): .verticalSection >
    //   .sectionTitleContainer.sectionTitleContainer-cards.padded-left > h2.sectionTitle.sectionTitle-cards,
    //   then is="emby-scroller" (.padded-top/bottom-focusscale, data-centerfocus) > .itemsContainer.scrollSlider.
    // padded-left is what insets the header to the cards' edge (the previously-missing inset); the real
    // emby-scroller insets the cards + adds native scroll arrows. Inner is a plain div (not
    // is="emby-itemscontainer") so its connectedCallback can't clear our pre-filled cards.
    function shelfHtml(shelf, items) {
        var byId = {};
        items.forEach(function (i) { byId[i.Id] = i; });
        var ordered = (shelf.Items || []).map(function (s) { return byId[s.Id]; }).filter(Boolean);
        if (!ordered.length) return "";
        var reason = shelf.Reason ? '<span class="gelo-reason">' + esc(shelf.Reason) + "</span>" : "";
        var cards = cardsHtml(ordered);
        return '<section class="verticalSection gelo-shelf">' +
            '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">' +
            '<h2 class="sectionTitle sectionTitle-cards">' + esc(shelf.Title) + reason + "</h2></div>" +
            '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale" data-centerfocus="true">' +
            '<div class="itemsContainer scrollSlider focuscontainer-x">' + cards + "</div></div>" +
            "</section>";
    }

    function railHtml(title, items, extraClass) {
        if (!items || !items.length) return "";
        var cards = cardsHtml(items);
        var cls = "verticalSection gelo-rail" + (extraClass ? " " + extraClass : "");
        return '<section class="' + cls + '">' +
            '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">' +
            '<h2 class="sectionTitle sectionTitle-cards">' + esc(title) + "</h2></div>" +
            '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale" data-centerfocus="true">' +
            '<div class="itemsContainer scrollSlider focuscontainer-x">' + cards + "</div></div>" +
            "</section>";
    }

    // Remove detached AND duplicate gelo hosts of one view so a container swap can never stack shelves.
    function purgeHosts(attr) {
        var sel = 'gelo-host[data-gelo="' + attr + '"]';
        var all = document.querySelectorAll(sel);
        var kept = false;
        for (var i = 0; i < all.length; i++) {
            var el = all[i];
            if (!el.isConnected || kept) el.remove();
            else kept = true;
        }
    }

    // ───────────────────────── Home shelves ─────────────────────────

    function homeContainer() {
        // jellyfin-web marks the real home container .homeSectionsContainer (homesections.js). Prefer it
        // explicitly; only fall back to "parent of the first native verticalSection" before that class is
        // applied. (Generic .verticalSection is NOT home-specific — Live TV / dashboard / plugin settings
        // all use it, which is why relying on it leaked Gelo rails onto those pages.)
        var c = document.querySelector(".homeSectionsContainer");
        if (c) return c;
        var first = document.querySelector(".verticalSection:not(.gelo-shelf)");
        return first ? first.parentElement : null;
    }

    function placeHome(host, container) {
        // Enforce the configured position each call. Native home sections load asynchronously
        // (Continue/Latest/NextUp stream in over several seconds), so for "bottom" we re-append to the
        // end on each tick to keep Gelo shelves pinned below them as they arrive. Moving an existing
        // node preserves its built children, and the self-mutation filter means our moves never
        // reschedule us — so this is flicker-free.
        if (host.parentElement !== container)
        {
            insertHomeAt(host, container);
            return;
        }
        if (POS === "top")
        {
            if (container.firstChild !== host) container.insertBefore(host, container.firstChild);
        }
        else if (POS === "bottom")
        {
            if (container.lastChild !== host) container.appendChild(host);
        }
        else
        {
            // afterFirst: sit right after the first native section.
            var first = container.querySelector(".verticalSection:not(.gelo-shelf)");
            var want = first ? first.nextSibling : null; // the node Gelo should precede
            if (want !== host)
            {
                if (want) container.insertBefore(host, want);
                else container.appendChild(host);
            }
        }
    }

    function insertHomeAt(host, container) {
        if (POS === "top") { container.insertBefore(host, container.firstChild); }
        else if (POS === "bottom") { container.appendChild(host); }
        else
        {
            var first = container.querySelector(".verticalSection:not(.gelo-shelf)");
            if (first && first.nextSibling) container.insertBefore(host, first.nextSibling);
            else container.appendChild(host);
        }
    }

    function injectHome() {
        if (!ready()) return;
        purgeHosts("home");

        // Already attached (and unique). Steady state: just re-pin to position (so "bottom" stays below
        // native sections as they stream in) — no rebuild, no refetch. placeHome is a no-op when the
        // host is already correctly placed, so this adds zero churn once the home settles.
        if (homeHostEl && homeHostEl.isConnected)
        {
            var settled = homeContainer();
            if (settled) placeHome(homeHostEl, settled);
            return;
        }

        var container = homeContainer();
        if (!container) return; // home not mounted yet; the observer will retry once React renders sections

        // React unmounted our host's old container but kept the element (children intact) — move it back.
        if (homeHostEl) { placeHome(homeHostEl, container); return; }

        // First build for this page session: reserve a placeholder, fetch, render.
        var host = document.createElement("div");
        host.className = "gelo-host gelo-loading";
        host.setAttribute("data-gelo", "home");
        placeHome(host, container);
        homeHostEl = host;

        // Fetch the categorized shelves AND the flat "For You" list in parallel. "Recommended For You"
        // is the engine's primary surface, so it leads the block. We omit ?variety= so the rail honors
        // the server's RecommendationsVariety default (off = static, medium/high = daily rotation). A
        // failure of the recs call must NOT take the shelves down with it, so it has its own catch.
        var shelvesP = geloGet("/Users/" + uid() + "/Shelves");
        var recsP = geloGet("/Users/" + uid() + "/Recommendations?limit=" + FORYOU_LIMIT + "&unwatched=true")
            .catch(function () { return []; });

        Promise.all([shelvesP, recsP]).then(function (results) {
            var shelves = (results[0] || []).slice(0, MAX);
            var recs = results[1] || [];

            // Nothing to show at all → pull the placeholder.
            if (!shelves.length && !recs.length) { dropHome(host); return; }

            var tasks = [];
            // The For You rail is tagged gelo-shelf so the home-positioning logic (which keys off
            // .verticalSection:not(.gelo-shelf) to find the first NATIVE section) treats it as part of
            // the Gelo block, never as a native section to position around.
            if (recs.length) {
                var recIds = recs.map(function (x) { return x.Id; }).filter(Boolean);
                tasks.push(hydrate(recIds).then(function (items) {
                    return railHtml(FORYOU_TITLE, items, "gelo-shelf");
                }));
            }
            shelves.forEach(function (sh) {
                var ids = (sh.Items || []).map(function (x) { return x.Id; }).filter(Boolean);
                tasks.push(hydrate(ids).then(function (items) { return shelfHtml(sh, items); }));
            });

            return Promise.all(tasks).then(function (htmls) {
                if (!host.isConnected) { homeHostEl = null; return; }
                host.classList.remove("gelo-loading");
                var html = htmls.filter(Boolean).join("");
                if (!html) { dropHome(host); return; }
                host.innerHTML = html;
                hydrateLazy(host);
            });
        }).catch(function () { dropHome(host); });
    }

    function dropHome(host) {
        homeHostEl = null;
        if (host && host.isConnected) host.remove();
    }

    // ───────────────────────── Detail "Recommended" rail ─────────────────────────

    function isDetail() { return /#\/details/.test(location.hash) || location.hash.indexOf("itemdetails.html") >= 0; }

    function detailId() {
        var m = location.hash.match(/[?&]id=([0-9a-fA-F-]{8,})/);
        return m ? m[1] : null;
    }

    function detailAnchor() {
        // Prefer the native "More Like This" row's parent so Gelo's rail sits right beside it;
        // fall back through the standard detail containers.
        var sim = document.querySelector("#similarCollapsible");
        if (sim && sim.parentElement) return sim.parentElement;
        return document.querySelector(".detailPageContent")
            || document.querySelector(".detailPagePrimaryContainer")
            || document.querySelector(".detailSection")
            || null;
    }

    function injectDetail() {
        if (!ready() || !isDetail()) return;
        var id = detailId();
        if (!id) return;
        purgeHosts("detail");

        // Navigated to a different item — discard the cached detail host so we fetch fresh recs.
        if (detailCacheId && detailCacheId !== id) {
            if (detailHostEl && detailHostEl.isConnected) detailHostEl.remove();
            detailHostEl = null;
            detailCacheId = null;
        }

        if (detailHostEl && detailHostEl.isConnected) return; // attached + correct item: no-op

        var anchor = detailAnchor();
        if (!anchor) return; // detail not fully rendered yet; observer will retry

        if (detailHostEl) { anchor.appendChild(detailHostEl); return; } // re-attach cached after eviction

        var host = document.createElement("div");
        host.className = "gelo-host gelo-loading";
        host.setAttribute("data-gelo", "detail");
        anchor.appendChild(host);
        detailHostEl = host;
        detailCacheId = id;

        geloGet("/Items/" + id + "/Similar?userId=" + uid() + "&limit=" + DETAIL_LIMIT).then(function (items) {
            // Similar returns SimilarItemDto[] (Id/Name/Type/Score); hydrate for card images.
            var ids = (items || []).map(function (x) { return x.Id; }).filter(Boolean);
            return hydrate(ids).then(function (hydrated) {
                if (!host.isConnected) { detailHostEl = null; return; }
                host.classList.remove("gelo-loading");
                var html = railHtml("Recommended", hydrated);
                if (!html) { dropDetail(host); return; }
                host.innerHTML = html;
                hydrateLazy(host);
            });
        }).catch(function () { dropDetail(host); });
    }

    function dropDetail(host) {
        if (detailHostEl === host) detailHostEl = null;
        if (host && host.isConnected) host.remove();
    }

    // ───────────────────────── Routing + observation ─────────────────────────

    function isHome() {
        if (isDetail()) return false;
        var h = location.hash;
        if (h === "" || h === "#" || h === "#/" || h.indexOf("#/home") >= 0) return true;
        // Strict fallback: ONLY the real home renders a .homeSectionsContainer. Generic .verticalSection
        // is used by Live TV, the dashboard, and plugin settings pages — matching it caused Gelo rails to
        // leak onto those pages, so it must not be treated as home.
        return !!document.querySelector(".homeSectionsContainer");
    }

    var tick = null;
    function schedule() {
        if (tick) return;
        tick = setTimeout(run, 400);
    }
    function run() {
        tick = null;
        try {
            if (isDetail()) injectDetail();
            else if (isHome()) injectHome();
        } catch (e) { /* never let a client bug disturb the page */ }
    }

    // A mutation counts as "ours" if it touches a .gelo-host (target or an added node). Ignoring these
    // is what breaks the observer→inject→observer feedback loop: our own writes never reschedule us.
    function isOurMutation(m) {
        var t = m.target;
        if (t && t.nodeType === 1 && t.closest && t.closest(".gelo-host")) return true;
        for (var j = 0; j < m.addedNodes.length; j++) {
            var n = m.addedNodes[j];
            if (n && n.nodeType === 1 && n.closest && n.closest(".gelo-host")) return true;
        }
        return false;
    }

    function start() {
        if (!document.body) { return setTimeout(start, 50); }
        var obs = new MutationObserver(function (muts) {
            for (var i = 0; i < muts.length; i++) {
                if (!isOurMutation(muts[i])) { schedule(); return; }
            }
        });
        obs.observe(document.documentElement, { childList: true, subtree: true });
        window.addEventListener("hashchange", schedule);
        window.addEventListener("popstate", schedule);
        schedule();
    }

    // Wait for the ApiClient to exist (it's set on login) before observing.
    function whenReady() {
        if (ready()) { start(); return; }
        setTimeout(whenReady, 400);
    }
    whenReady();
})();
