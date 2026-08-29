from __future__ import annotations

import argparse
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageColor, ImageDraw

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


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_color(value: str | None) -> tuple[int, int, int, int]:
    if not value or value in {"none", "currentColor"}:
        return 255, 255, 255, 255
    rgb = ImageColor.getrgb(value)
    return (*rgb[:3], 255)


PATH_TOKEN = re.compile(r"[AaCcHhLlMmQqSsTtVvZz]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def parse_svg_path(data: str) -> list[list[tuple[float, float]]]:
    """Flatten the SVG path commands used by the original Bootstrap icons."""
    tokens = PATH_TOKEN.findall(data)
    paths: list[list[tuple[float, float]]] = []
    current_path: list[tuple[float, float]] = []
    index = 0
    command = ""
    x = y = 0.0
    start_x = start_y = 0.0
    last_control: tuple[float, float] | None = None
    previous_command = ""

    def is_command(value: str) -> bool:
        return len(value) == 1 and value.isalpha()

    def number() -> float:
        nonlocal index
        value = float(tokens[index])
        index += 1
        return value

    def add_point(px: float, py: float) -> None:
        nonlocal x, y
        x, y = px, py
        if not current_path or current_path[-1] != (x, y):
            current_path.append((x, y))

    def flush() -> None:
        nonlocal current_path
        if len(current_path) >= 2:
            paths.append(current_path)
        current_path = []

    def cubic(p0, p1, p2, p3, steps: int = 18) -> None:
        for step in range(1, steps + 1):
            t = step / steps
            u = 1.0 - t
            add_point(
                u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1],
            )

    def quadratic(p0, p1, p2, steps: int = 14) -> None:
        for step in range(1, steps + 1):
            t = step / steps
            u = 1.0 - t
            add_point(
                u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
                u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1],
            )

    def arc(rx: float, ry: float, rotation: float, large_arc: bool, sweep: bool, end_x: float, end_y: float) -> None:
        nonlocal x, y
        if rx == 0 or ry == 0 or (x == end_x and y == end_y):
            add_point(end_x, end_y)
            return
        rx, ry = abs(rx), abs(ry)
        phi = math.radians(rotation % 360.0)
        cos_phi, sin_phi = math.cos(phi), math.sin(phi)
        dx, dy = (x - end_x) / 2.0, (y - end_y) / 2.0
        x1p = cos_phi * dx + sin_phi * dy
        y1p = -sin_phi * dx + cos_phi * dy
        scale = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry)
        if scale > 1.0:
            factor = math.sqrt(scale)
            rx *= factor
            ry *= factor
        numerator = max(0.0, rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p)
        denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        coefficient = 0.0 if denominator == 0 else math.sqrt(numerator / denominator)
        if large_arc == sweep:
            coefficient = -coefficient
        cxp = coefficient * rx * y1p / ry
        cyp = coefficient * -ry * x1p / rx
        center_x = cos_phi * cxp - sin_phi * cyp + (x + end_x) / 2.0
        center_y = sin_phi * cxp + cos_phi * cyp + (y + end_y) / 2.0

        def angle(ux: float, uy: float, vx: float, vy: float) -> float:
            return math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)

        ux, uy = (x1p - cxp) / rx, (y1p - cyp) / ry
        vx, vy = (-x1p - cxp) / rx, (-y1p - cyp) / ry
        theta = math.atan2(uy, ux)
        delta = angle(ux, uy, vx, vy)
        if not sweep and delta > 0:
            delta -= 2 * math.pi
        elif sweep and delta < 0:
            delta += 2 * math.pi
        steps = max(4, math.ceil(abs(delta) / (math.pi / 18.0)))
        for step in range(1, steps + 1):
            value = theta + delta * step / steps
            cos_value, sin_value = math.cos(value), math.sin(value)
            add_point(
                center_x + cos_phi * rx * cos_value - sin_phi * ry * sin_value,
                center_y + sin_phi * rx * cos_value + cos_phi * ry * sin_value,
            )

    while index < len(tokens):
        if is_command(tokens[index]):
            command = tokens[index]
            index += 1
        if not command:
            raise ValueError("SVG path begins without a command")
        relative = command.islower()
        upper = command.upper()
        if upper == "Z":
            add_point(start_x, start_y)
            flush()
            command = ""
            last_control = None
            previous_command = "Z"
            continue
        if upper == "M":
            px, py = number(), number()
            if relative:
                px += x
                py += y
            if current_path:
                flush()
            add_point(px, py)
            start_x, start_y = x, y
            command = "l" if relative else "L"
            last_control = None
            previous_command = "M"
        elif upper == "L":
            px, py = number(), number()
            add_point(x + px if relative else px, y + py if relative else py)
            last_control = None
            previous_command = "L"
        elif upper == "H":
            px = number()
            add_point(x + px if relative else px, y)
            last_control = None
            previous_command = "H"
        elif upper == "V":
            py = number()
            add_point(x, y + py if relative else py)
            last_control = None
            previous_command = "V"
        elif upper == "C":
            values = [number() for _ in range(6)]
            if relative:
                control1 = (x + values[0], y + values[1])
                control2 = (x + values[2], y + values[3])
                endpoint = (x + values[4], y + values[5])
            else:
                control1, control2, endpoint = (values[0], values[1]), (values[2], values[3]), (values[4], values[5])
            cubic((x, y), control1, control2, endpoint)
            last_control = control2
            previous_command = "C"
        elif upper == "S":
            values = [number() for _ in range(4)]
            control1 = (2 * x - last_control[0], 2 * y - last_control[1]) if previous_command in {"C", "S"} and last_control else (x, y)
            control2 = (x + values[0], y + values[1]) if relative else (values[0], values[1])
            endpoint = (x + values[2], y + values[3]) if relative else (values[2], values[3])
            cubic((x, y), control1, control2, endpoint)
            last_control = control2
            previous_command = "S"
        elif upper == "Q":
            values = [number() for _ in range(4)]
            control = (x + values[0], y + values[1]) if relative else (values[0], values[1])
            endpoint = (x + values[2], y + values[3]) if relative else (values[2], values[3])
            quadratic((x, y), control, endpoint)
            last_control = control
            previous_command = "Q"
        elif upper == "T":
            values = [number() for _ in range(2)]
            control = (2 * x - last_control[0], 2 * y - last_control[1]) if previous_command in {"Q", "T"} and last_control else (x, y)
            endpoint = (x + values[0], y + values[1]) if relative else (values[0], values[1])
            quadratic((x, y), control, endpoint)
            last_control = control
            previous_command = "T"
        elif upper == "A":
            values = [number() for _ in range(7)]
            endpoint = (x + values[5], y + values[6]) if relative else (values[5], values[6])
            arc(values[0], values[1], values[2], bool(values[3]), bool(values[4]), *endpoint)
            last_control = None
            previous_command = "A"
        else:
            raise ValueError(f"Unsupported SVG path command: {command}")
    flush()
    return paths


def render_icon(source: Path, destination: Path, size: int, supersample: int) -> None:
    root = ET.parse(source).getroot()
    view_box = [float(value) for value in root.attrib.get("viewBox", "0 0 32 32").split()]
    if len(view_box) != 4 or view_box[2] <= 0 or view_box[3] <= 0:
        raise ValueError(f"Invalid viewBox in {source}")

    canvas_size = size * supersample
    scale_x = canvas_size / view_box[2]
    scale_y = canvas_size / view_box[3]
    origin_x, origin_y = view_box[0], view_box[1]
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def point(x: float, y: float) -> tuple[float, float]:
        return (x - origin_x) * scale_x, (y - origin_y) * scale_y

    for element in root.iter():
        tag = local_name(element.tag)
        fill = parse_color(element.attrib.get("fill"))
        if tag == "rect":
            x = float(element.attrib.get("x", 0))
            y = float(element.attrib.get("y", 0))
            width = float(element.attrib.get("width", 0))
            height = float(element.attrib.get("height", 0))
            draw.rectangle([point(x, y), point(x + width, y + height)], fill=fill)
        elif tag == "circle":
            cx = float(element.attrib.get("cx", 0))
            cy = float(element.attrib.get("cy", 0))
            radius = float(element.attrib.get("r", 0))
            draw.ellipse([point(cx - radius, cy - radius), point(cx + radius, cy + radius)], fill=fill)
        elif tag == "polygon":
            numbers = [
                float(value)
                for value in re.split(r"[\s,]+", element.attrib.get("points", "").strip())
                if value
            ]
            if len(numbers) < 6 or len(numbers) % 2:
                raise ValueError(f"Invalid polygon in {source}")
            draw.polygon([point(numbers[index], numbers[index + 1]) for index in range(0, len(numbers), 2)], fill=fill)
        elif tag == "path":
            for subpath in parse_svg_path(element.attrib.get("d", "")):
                draw.polygon([point(x, y) for x, y in subpath], fill=fill)

    destination.parent.mkdir(parents=True, exist_ok=True)
    image.resize((size, size), Image.Resampling.LANCZOS).save(destination, "PNG")
    destination.with_suffix(".vtex").write_text(
        VTEX_TEMPLATE.replace("{name}", source.stem),
        encoding="utf-8",
        newline="\n",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="Rasterize SVG icon masters to PNG for VTEX compilation.")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--size", type=int, default=64)
    parser.add_argument("--supersample", type=int, default=4)
    args = parser.parse_args()
    if args.size < 16 or args.size > 512 or args.supersample < 1 or args.supersample > 8:
        parser.error("size or supersample is outside the supported range")

    sources = sorted(args.source.glob("*.svg"))
    if not sources:
        parser.error(f"no SVG icons found in {args.source}")
    for source in sources:
        destination = args.output / f"{source.stem}.png"
        render_icon(source, destination, args.size, args.supersample)
        print(f"{source.name} -> {destination.name} + {destination.with_suffix('.vtex').name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
