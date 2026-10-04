import { ImageZoom } from 'fumadocs-ui/components/image-zoom';
import { withBase } from '@/lib/shared';

/** A screenshot of the plugin from public/images, with a caption; opens full size on click. */
export function Screenshot({ src, alt, caption, width }: { src: string; alt: string; caption?: string; width?: number }) {
  return (
    <figure className="not-prose my-6">
      <ImageZoom src={withBase(`/images/${src}`)} alt={alt} width={width ?? 1200} height={800} className="xiv-shot h-auto w-full" style={width ? { maxWidth: width } : undefined} />
      {caption && <figcaption className="mt-2 text-center text-sm text-fd-muted-foreground">{caption}</figcaption>}
    </figure>
  );
}

/** The XIV MCP mark, centred. */
export function BrandLogo({ size = 96 }: { size?: number }) {
  return (
    <img
      src={withBase('/logo.svg')}
      alt="The XIV MCP mark: a crystal as the body of a satellite"
      width={size}
      height={size}
      className="not-prose mx-auto my-6 drop-shadow-[0_0_30px_rgba(59,239,196,0.25)]"
    />
  );
}
