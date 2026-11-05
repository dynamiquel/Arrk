(() => {
  "use strict";

  const header = document.querySelector(".site-header");
  const menuToggle = document.querySelector(".menu-toggle");
  const nav = document.querySelector(".site-nav");

  if (menuToggle && header) {
    menuToggle.addEventListener("click", () => {
      const open = header.classList.toggle("is-open");
      menuToggle.setAttribute("aria-expanded", String(open));
    });

    document.addEventListener("click", (event) => {
      if (!header.classList.contains("is-open")) return;
      if (header.contains(event.target)) return;
      header.classList.remove("is-open");
      menuToggle.setAttribute("aria-expanded", "false");
    });

    document.addEventListener("keydown", (event) => {
      if (event.key !== "Escape" || !header.classList.contains("is-open")) return;
      header.classList.remove("is-open");
      menuToggle.setAttribute("aria-expanded", "false");
      menuToggle.focus();
    });

    if (nav) {
      nav.addEventListener("click", (event) => {
        if (!(event.target instanceof Element) || !event.target.closest("a")) return;
        header.classList.remove("is-open");
        menuToggle.setAttribute("aria-expanded", "false");
      });
    }
  }

  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      const target = button.closest(".code-block")?.querySelector("pre");
      if (!target) return;

      const text = target.innerText;
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        const area = document.createElement("textarea");
        area.value = text;
        area.style.position = "fixed";
        area.style.opacity = "0";
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
      }

      const label = button.querySelector("span");
      const previous = label ? label.textContent : "";
      button.classList.add("is-copied");
      if (label) label.textContent = "Copied";

      window.setTimeout(() => {
        button.classList.remove("is-copied");
        if (label) label.textContent = previous || "Copy";
      }, 1600);
    });
  });

  document.querySelectorAll("[data-year]").forEach((node) => {
    node.textContent = String(new Date().getFullYear());
  });

  // Mark the current page in the shared navigation.
  const current = (window.location.pathname.split("/").pop() || "index.html").toLowerCase();
  document.querySelectorAll(".site-nav a").forEach((link) => {
    const href = (link.getAttribute("href") || "").toLowerCase();
    if (href === current || (current === "" && href === "index.html")) {
      link.classList.add("is-active");
      link.setAttribute("aria-current", "page");
    }
  });

  // Gentle entrance animation, disabled by prefers-reduced-motion in CSS.
  const reveals = document.querySelectorAll(".reveal");
  if (reveals.length && "IntersectionObserver" in window) {
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      });
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.05 });

    reveals.forEach((node) => observer.observe(node));
  } else {
    reveals.forEach((node) => node.classList.add("is-visible"));
  }

  // Guide table of contents follows the section being read.
  const guideSections = document.querySelectorAll(".guide-section[id]");
  const guideLinks = document.querySelectorAll(".guide-nav a");
  if (guideSections.length && guideLinks.length && "IntersectionObserver" in window) {
    const linkFor = (id) => document.querySelector(`.guide-nav a[href="#${CSS.escape(id)}"]`);
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        guideLinks.forEach((link) => link.classList.remove("is-active"));
        const link = linkFor(entry.target.id);
        if (link) link.classList.add("is-active");
      });
    }, { rootMargin: "-18% 0px -68% 0px", threshold: 0 });

    guideSections.forEach((section) => observer.observe(section));
  }

  // FAQ filter.
  const faqSearch = document.querySelector("[data-faq-search]");
  if (faqSearch) {
    const items = Array.from(document.querySelectorAll(".faq-item"));
    const groups = Array.from(document.querySelectorAll(".faq-group"));
    const empty = document.querySelector(".faq-empty");

    const filter = () => {
      const query = faqSearch.value.trim().toLowerCase();
      let visible = 0;

      items.forEach((item) => {
        const match = !query || item.textContent.toLowerCase().includes(query);
        item.hidden = !match;
        if (match) visible += 1;
      });

      groups.forEach((group) => {
        const hasVisible = Array.from(group.querySelectorAll(".faq-item")).some((item) => !item.hidden);
        group.hidden = !hasVisible;
      });

      if (empty) empty.style.display = visible === 0 ? "block" : "none";
    };

    faqSearch.addEventListener("input", filter);
  }
})();
