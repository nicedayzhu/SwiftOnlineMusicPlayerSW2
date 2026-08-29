from __future__ import annotations

import argparse
import pathlib
import shutil
import subprocess
import tempfile
import time

VTEX_TEMPLATE = """<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
"CDmeVtex"
{
    "m_inputTextureArray" "element_array"
    [
        "CDmeInputTexture"
        {
            "m_name" "string" "InputTexture0"
            "m_fileName" "string" "panorama/images/custom_game/music_player/{name}.png"
            "m_colorSpace" "string" "srgb"
            "m_typeString" "string" "2D"
            "m_imageProcessorArray" "element_array"
            [
                "CDmeImageProcessor"
                {
                    "m_algorithm" "string" "None"
                    "m_stringArg" "string" ""
                    "m_vFloat4Arg" "vector4" "0 0 0 0"
                }
            ]
        }
    ]
    "m_outputTypeString" "string" "2D"
    "m_outputFormat" "string" "BGRA8888"
    "m_outputClearColor" "vector4" "0 0 0 0"
    "m_nOutputMinDimension" "int" "0"
    "m_nOutputMaxDimension" "int" "64"
    "m_textureOutputChannelArray" "element_array"
    [
        "CDmeTextureOutputChannel"
        {
            "m_inputTextureArray" "string_array" [ "InputTexture0" ]
            "m_srcChannels" "string" "rgba"
            "m_dstChannels" "string" "rgba"
            "m_mipAlgorithm" "CDmeImageProcessor"
            {
                "m_algorithm" "string" "Box"
                "m_stringArg" "string" ""
                "m_vFloat4Arg" "vector4" "0 0 0 0"
            }
            "m_outputColorSpace" "string" "srgb"
        }
    ]
    "m_vClamp" "vector3" "0 0 0"
    "m_bNoLod" "bool" "1"
}
"""


def find_browser() -> str:
    candidates = [
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ]
    for candidate in candidates:
        if pathlib.Path(candidate).exists():
            return candidate
    raise RuntimeError(
        "No Chromium browser found. Install Edge or Chrome, or provide the browser path."
    )


def build_html(svg_text: str, pixel_size: int) -> str:
    return (
        "<!doctype html><html><head><meta charset=\"utf-8\"><style>"
        "html,body{margin:0;padding:0;background:transparent}"
        f"svg{{display:block;width:{pixel_size}px;height:{pixel_size}px}}"
        "</style></head><body>"
        f"{svg_text}"
        "</body></html>"
    )


def rasterize_svg(
    browser: str,
    svg_path: pathlib.Path,
    png_path: pathlib.Path,
    pixel_size: int,
) -> None:
    svg_text = svg_path.read_text(encoding="utf-8")
    html = build_html(svg_text, pixel_size)
    with tempfile.TemporaryDirectory() as work:
        html_path = pathlib.Path(work) / "icon.html"
        html_path.write_text(html, encoding="utf-8")
        screenshot = pathlib.Path(work) / "out.png"
        png_path.parent.mkdir(parents=True, exist_ok=True)
        command = [
            browser,
            "--headless",
            "--disable-gpu",
            "--hide-scrollbars",
            "--no-first-run",
            "--no-default-browser-check",
            "--default-background-color=00000000",
            f"--window-size={pixel_size},{pixel_size}",
            f"--screenshot={screenshot}",
            "file:///" + html_path.as_posix(),
        ]
        subprocess.run(command, check=False, capture_output=True, timeout=60)
        # The browser may write asynchronously; poll briefly for the output.
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            if screenshot.exists() and screenshot.stat().st_size > 0:
                break
            time.sleep(0.15)
        if not screenshot.exists():
            raise RuntimeError(f"Edge produced no screenshot for {svg_path.name}")
        shutil.copyfile(screenshot, png_path)


def main() -> int:
    parser = argparse.ArgumentParser(description="Rasterize icon SVGs to PNG via a Chromium browser.")
    parser.add_argument("--source", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--size", required=True, type=int)
    args = parser.parse_args()

    svgs = sorted(args.source.glob("*.svg"))
    if not svgs:
        raise SystemExit(f"No .svg files found under {args.source}")

    browser = find_browser()
    for svg in svgs:
        name = svg.stem
        png = args.output / f"{name}.png"
        rasterize_svg(browser, svg, png, args.size)
        args.output.mkdir(parents=True, exist_ok=True)
        png.write_bytes(png.read_bytes())
        (args.output / f"{name}.vtex").write_text(
            VTEX_TEMPLATE.replace("{name}", name), encoding="utf-8"
        )
        print(f"{name}.svg -> {name}.png + {name}.vtex")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
