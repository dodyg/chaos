// Read-only view of a pad drawing (strokes stored as {"v":1,"strokes":[{tool,color,size,points:[[x,y,pressure]]}]}).
// Points are in the units of the canvas the drawing was made on, so the SVG's viewBox is that canvas and it scales to any screen.
const colorPattern = /^#[0-9a-fA-F]{6}$/;
const highlighterScale = 3;

export function parseStrokes(json) {
  try {
    const raw = JSON.parse(json);
    if (!raw || !Array.isArray(raw.strokes)) return [];
    return raw.strokes.flatMap((item) => {
      if (!item || !Array.isArray(item.points)) return [];
      const points = item.points.filter((p) => Array.isArray(p) && Number.isFinite(p[0]) && Number.isFinite(p[1]));
      if (points.length === 0) return [];
      return [
        {
          highlighter: item.tool === "highlighter",
          color: typeof item.color === "string" && colorPattern.test(item.color) ? item.color : "#111827",
          size: typeof item.size === "number" && item.size > 0 && item.size <= 200 ? item.size : 6,
          points,
        },
      ];
    });
  } catch {
    return [];
  }
}

// A smooth line through the points: quadratic curves between midpoints.
export function strokePath(points) {
  const f = (n) => Math.round(n * 100) / 100;
  if (points.length === 1) return `M ${f(points[0][0])} ${f(points[0][1])} h 0.01`;
  let d = `M ${f(points[0][0])} ${f(points[0][1])}`;
  for (let i = 1; i < points.length - 1; i++) {
    const [x, y] = points[i];
    const [nx, ny] = points[i + 1];
    d += ` Q ${f(x)} ${f(y)} ${f((x + nx) / 2)} ${f((y + ny) / 2)}`;
  }
  const last = points[points.length - 1];
  return `${d} L ${f(last[0])} ${f(last[1])}`;
}

export default function DrawingView({ strokes, width, height }) {
  const parsed = parseStrokes(strokes ?? "");
  const w = width > 0 ? width : 1600;
  const h = height > 0 ? height : 1200;
  if (parsed.length === 0) return <p className="reading-meta">This drawing is empty.</p>;
  return (
    <svg className="drawing-view" viewBox={`0 0 ${w} ${h}`} role="img" aria-label="Drawing">
      <rect width={w} height={h} fill="#ffffff" />
      {parsed.map((stroke, index) => (
        <path
          key={index}
          d={strokePath(stroke.points)}
          fill="none"
          stroke={stroke.color}
          strokeWidth={stroke.highlighter ? stroke.size * highlighterScale : stroke.size}
          strokeOpacity={stroke.highlighter ? 0.35 : 1}
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      ))}
    </svg>
  );
}
