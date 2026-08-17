#!/usr/bin/env python3
"""Persian music index fetcher (musics-fa.com / upmusics.com).

Uses curl_cffi browser TLS impersonation to bypass the sites' anti-bot, and a
SOCKS5 proxy for the site domains while the media CDNs are fetched DIRECT (the
CDNs reject the proxy's TLS but accept direct impersonated requests).

Modes:
  search <site> <query> <limit>     → JSON list of {url, title} post pages
  links  <post-url>                 → JSON list of {url, quality} mp3 links
  dl     <mp3-url> <out-path>       → stream download, prints progress lines

Site keys: musicfa | upmusics | taksong
"""
import json
import re
import socket
import sys
from urllib.parse import quote, unquote

from curl_cffi import requests

PROXY = {"http": "socks5://127.0.0.1:10808", "https": "socks5://127.0.0.1:10808"}
IMP = "chrome"
TIMEOUT = 30

SITES = {
    "musicfa": {
        "search": "https://www.music-fa.com/?s={q}",
        "post_re": r'href="(https://musics-fa\.com/download-song/\d+/)"',
        "cdn_hosts": ["dls.musics-fa.com", "dls.music-fa.com"],
        "quality_hint": ["(320)", "%20320", "(128)", "%20128"],
    },
    "musicsfa": {
        "search": "https://musics-fa.com/?s={q}",
        "post_re": r'href="(https://musics-fa\.com/download-song/\d+/)"',
        "cdn_hosts": ["dls.musics-fa.com"],
        "quality_hint": ["(320)", "%20320", "(128)", "%20128"],
    },
    "upmusics": {
        "search": "https://upmusics.com/?s={q}",
        "post_re": r'href="(https://upmusics\.com/[^"]+)"',
        "cdn_hosts": ["irsv.upmusics.com"],
        "quality_hint": [],
    },
}


def _get(url, use_proxy=True, stream=False, impersonate=IMP):
    p = PROXY if use_proxy else None
    try:
        return requests.get(url, impersonate=impersonate, timeout=TIMEOUT,
                            proxies=p, stream=stream, allow_redirects=True)
    except Exception:
        if not p:
            raise
        # proxy dead/filtered — many of these sites are reachable directly
        return requests.get(url, impersonate=impersonate, timeout=TIMEOUT,
                            proxies=None, stream=stream, allow_redirects=True)


def do_search(site_key, query, limit):
    site = SITES[site_key]
    url = site["search"].format(q=quote(query))
    try:
        r = _get(url)
    except Exception as e:
        print(json.dumps({"error": f"search request failed: {e}"}))
        return
    body = r.text
    noise = ("wp-", "#", "?", "xmlrpc", "/tag/", "/category/", "/author/",
             "/page/", "release-music", "contact-us", "about-us",
             "/artist/", "/feed", "/album/", "/music-video/", "/podcast/")
    # Result items live in <article> blocks on these WP themes — the bare
    # post_re also catches sidebar "latest posts" links, which is noise.
    posts = re.findall(r'<article[^>]*>.*?href="(https?://[^"\s]+)"',
                       body, re.S)
    posts = [p for p in posts if not any(x in p for x in noise)]
    if not posts:
        posts = re.findall(site["post_re"], body)
        posts = [p for p in posts if not any(x in p for x in noise)]
    posts = list(dict.fromkeys(posts))
    results = []
    for p in posts[:limit]:
        slug = unquote(p.rstrip("/").split("/")[-1])
        title = slug.replace("-", " ").replace("+", " ").strip()
        results.append({"url": p, "title": title})
    print(json.dumps({"posts": results, "count": len(results)}))


def do_links(post_url):
    try:
        r = _get(post_url)
    except Exception as e:
        print(json.dumps({"error": f"post fetch failed: {e}"}))
        return
    body = r.text
    mp3s = list(dict.fromkeys(re.findall(r'(https?://[^"\'\s<>]+\.mp3[^"\'\s<>]*)', body)))
    # strip cache-busting query strings
    cleaned = []
    for m in mp3s:
        base = m.split("?")[0]
        if base not in [c["url"] for c in cleaned]:
            q = "320" if ("(320)" in m or "%20320" in m) else ("128" if ("(128)" in m or "%20128" in m) else "?")
            cleaned.append({"url": base, "quality": q})
    # title from <title>
    t = re.search(r"<title>([^<]+)</title>", body)
    title = t.group(1).strip() if t else ""
    print(json.dumps({"mp3s": cleaned, "title": title, "count": len(cleaned)}))


def do_dl(mp3_url, out_path):
    try:
        r = _get(mp3_url, use_proxy=False, stream=True)
        if r.status_code != 200:
            print(json.dumps({"error": f"HTTP {r.status_code}"}))
            return 1
        total = int(r.headers.get("content-length") or 0)
        done = 0
        try:
            with open(out_path, "wb") as f:
                for chunk in r.iter_content(chunk_size=65536):
                    if not chunk:
                        continue
                    f.write(chunk)
                    done += len(chunk)
                    if total:
                        pct = done * 100 // total
                        print(f"PROGRESS {pct} {done} {total}", flush=True)
        finally:
            try:
                r.close()
            except Exception:
                pass
        print(json.dumps({"ok": True, "bytes": done, "path": out_path}))
        return 0
    except Exception as e:
        print(json.dumps({"error": f"download failed: {e}"}))
        return 1


def main():
    args = sys.argv[1:]
    # optional --proxy <url> (use "-" for none) before the mode argument
    global PROXY
    if args and args[0] == "--proxy":
        if len(args) < 2:
            print(json.dumps({"error": "--proxy requires a value"}))
            return 1
        if args[1] != "-":
            PROXY = {"http": args[1], "https": args[1]}
            # A dead-but-configured proxy makes every request hang to timeout —
            # probe it once (2s) and fall back to direct when it isn't listening.
            try:
                url = args[1].split("://", 1)[-1]
                host, port = url.rsplit(":", 1)
                s = socket.create_connection((host, int(port)), timeout=2)
                s.close()
            except Exception:
                PROXY = None
        else:
            PROXY = None
        args = args[2:]
    if len(args) < 1:
        print(json.dumps({"error": "usage: persian_fetch.py [--proxy url|-] <search|links|dl> ..."}))
        return 1
    mode = args[0]
    try:
        if mode == "search":
            do_search(args[1], args[2], int(args[3]))
        elif mode == "links":
            do_links(args[1])
        elif mode == "dl":
            return do_dl(args[1], args[2])
        else:
            print(json.dumps({"error": f"unknown mode {mode}"}))
            return 1
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
