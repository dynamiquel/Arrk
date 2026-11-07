"""Hermetic checks: python3 site/test-product-guides.py (no server or packages required)."""

import importlib.util
from html.parser import HTMLParser
from pathlib import Path
import re
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("build_guides", Path(__file__).with_name("build-guides.py"))
GUIDES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GUIDES)


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = []
        self.links = []

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if "id" in attributes:
            self.ids.append(attributes["id"])
        if tag == "a" and "href" in attributes:
            self.links.append(attributes["href"])


class ProductGuidePageTests(unittest.TestCase):
    def test_generated_page_matches_single_source(self):
        self.assertEqual(GUIDES.OUTPUT.read_text(encoding="utf-8"), GUIDES.render())

    def test_every_local_link_resolves_and_ids_are_unique(self):
        page = Links()
        page.feed(GUIDES.render())
        self.assertEqual(len(page.ids), len(set(page.ids)))
        for link in page.links:
            if link.startswith("#"):
                self.assertIn(link[1:], page.ids)
            elif "://" not in link:
                self.assertTrue((GUIDES.SITE / link).is_file(), link)

    def test_all_sources_and_sections_are_present(self):
        page = Links()
        page.feed(GUIDES.render())
        sources = list(GUIDES.SOURCE.glob("*/Documentation/*.md"))
        self.assertEqual(len(sources), 9)
        for source in sources:
            self.assertIn(GUIDES.topic_id(source), page.ids)
            text = source.read_text(encoding="utf-8")
            for block in text.strip().split("\n\n")[1:]:
                if block.startswith("- ") or re.match(r"\d+\. ", block):
                    for line in block.splitlines():
                        item = re.sub(r"^(?:- |\d+\. )", "", line)
                        self.assertIn(f"<li>{GUIDES.inline(item)}</li>", GUIDES.render())
                elif not block.startswith("## "):
                    self.assertIn(f"<p>{GUIDES.inline(block)}</p>", GUIDES.render())

    def test_steps_render_as_safe_numbered_and_bulleted_lists(self):
        with TemporaryDirectory() as folder:
            source = Path(folder)
            documents = source / "SampleModule" / "Documentation"
            documents.mkdir(parents=True)
            (documents / "start.md").write_text(
                "# Start here\n\nA short introduction.\n\n## Connect\n\n"
                "1. Open `Configuration`.\n2. Keep <password> private.\n\n"
                "- Choose a provider.\n- Test the connection.\n", encoding="utf-8")
            with patch.object(GUIDES, "SOURCE", source):
                rendered = GUIDES.render()
            self.assertIn("<ol><li>Open <code>Configuration</code>.</li>\n"
                          "<li>Keep &lt;password&gt; private.</li></ol>", rendered)
            self.assertIn("<ul><li>Choose a provider.</li>\n"
                          "<li>Test the connection.</li></ul>", rendered)
            self.assertNotIn("<password>", rendered)

    def test_source_html_is_never_executed(self):
        rendered = GUIDES.inline('<script>alert("x")</script> `a < b`')
        self.assertNotIn("<script>", rendered)
        self.assertIn("&lt;script&gt;", rendered)
        self.assertIn("<code>a &lt; b</code>", rendered)

    def test_modules_can_publish_the_same_filename(self):
        with TemporaryDirectory() as folder:
            source = Path(folder)
            for name in ("Arrk.First", "Arrk.Second"):
                documents = source / name / "Documentation"
                documents.mkdir(parents=True)
                (documents / "start.md").write_text("# Start\n\nInstructions.\n", encoding="utf-8")
            with patch.object(GUIDES, "SOURCE", source):
                page = Links()
                page.feed(GUIDES.render())
            self.assertIn("first-start", page.ids)
            self.assertIn("second-start", page.ids)
            self.assertEqual(len(page.ids), len(set(page.ids)))


if __name__ == "__main__":
    unittest.main()
