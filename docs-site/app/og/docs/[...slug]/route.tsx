import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { source } from '@/lib/source';
import { notFound } from 'next/navigation';
import { generateOGImage } from 'fumadocs-ui/og';
import { appName, getPageImageUrl } from '@/lib/shared';

export const revalidate = false;

// Satori needs images inline; the images are rendered at build time, so the logo is read from disk.
const logo = readFile(join(process.cwd(), 'public', 'logo.png')).then((b) => `data:image/png;base64,${b.toString('base64')}`);

export async function GET(_req: Request, { params }: RouteContext<'/og/docs/[...slug]'>) {
  const { slug } = await params;
  const page = source.getPage(slug.slice(0, -1));
  if (!page) notFound();

  return generateOGImage({
    title: page.data.title,
    description: page.data.description,
    site: appName,
    primaryColor: 'rgba(63, 123, 255, 0.35)',
    primaryTextColor: 'rgb(59, 239, 196)',
    icon: <img src={await logo} alt="" width={64} height={64} />,
  });
}

export function generateStaticParams() {
  return source.getPages().map((page) => ({
    lang: page.locale,
    slug: getPageImageUrl(page).segments,
  }));
}
