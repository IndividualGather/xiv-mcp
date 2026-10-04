import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { appName, repoUrl, withBase } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="inline-flex items-center gap-2 font-semibold tracking-tight">
          <img src={withBase('/logo.svg')} alt="" width={26} height={26} />
          <span className="xiv-wordmark">{appName}</span>
        </span>
      ),
    },
    githubUrl: repoUrl,
  };
}
