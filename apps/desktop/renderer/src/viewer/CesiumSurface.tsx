import { Color } from "cesium";

const cesiumVersion = "1.144.0";

export function CesiumSurface() {
  return (
    <section
      className="viewer-surface"
      aria-label="二维与三维成果查看器"
      style={{ backgroundColor: Color.fromCssColorString("#07101b").toCssColorString() }}
    >
      <div className="viewer-surface__grid" aria-hidden="true" />
      <div className="viewer-surface__message">
        <span>CesiumJS {cesiumVersion}</span>
        <strong>成果查看器边界已就绪</strong>
        <p>任务 2.1 不加载外部底图、用户数据或网络资源。</p>
      </div>
    </section>
  );
}
