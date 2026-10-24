// Deterministic callouts over unmodified native/browser captures. Requires sharp.
// NODE_PATH may point to an existing installation; no image generation is used.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');
const root = path.resolve(__dirname, '../docs/images/walkthrough');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

async function main() {
  const manifest = JSON.parse(await fs.readFile(path.join(root, 'callouts.json'), 'utf8'));
  for (const figure of manifest.figures) {
    const raw = await fs.readFile(path.join(root, 'raw', figure.source + '.png'));
    const metadata = await sharp(raw).metadata();
    const [left, top, width, height] = figure.crop || [0, 0, metadata.width, metadata.height];
    const pad = 32;
    const layers = [];
    for (const [index, point] of figure.points.entries()) {
      const [x, y, w, h] = point.rect;
      const [mx, my] = point.marker;
      if (x < left || y < top || x + w > left + width || y + h > top + height) {
        throw new Error(`${figure.name}: callout ${index + 1} outside crop`);
      }
      const rx = x - left + pad, ry = y - top + pad;
      const cx = mx - left + pad, cy = my - top + pad;
      const tx = Math.max(rx, Math.min(cx, rx + w));
      const ty = Math.max(ry, Math.min(cy, ry + h));
      layers.push(`<path d="M${cx},${cy} L${tx},${ty}" stroke="#111827" stroke-width="7"/><path d="M${cx},${cy} L${tx},${ty}" stroke="#ffd166" stroke-width="3"/>`);
      layers.push(`<rect x="${rx}" y="${ry}" width="${w}" height="${h}" rx="4" fill="none" stroke="#111827" stroke-width="6"/><rect x="${rx}" y="${ry}" width="${w}" height="${h}" rx="4" fill="none" stroke="#ffd166" stroke-width="3"/>`);
      layers.push(`<circle cx="${cx}" cy="${cy}" r="16" fill="#ffd166" stroke="#111827" stroke-width="3"/><text x="${cx}" y="${cy + 7}" text-anchor="middle" font-family="Arial,sans-serif" font-weight="bold" font-size="21" fill="#111827">${index + 1}</text>`);
    }
    const overlay = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${width + 2 * pad}" height="${height + 2 * pad}">${layers.join('')}</svg>`);
    const output = await sharp(raw).extract({left, top, width, height})
      .extend({top:pad, bottom:pad, left:pad, right:pad, background:'#18212f'})
      .composite([{input:overlay}]).png().toBuffer();
    await fs.writeFile(path.join(root, figure.name + '.png'), output);
    figure.rawSHA256 = hash(raw);
    figure.annotatedSHA256 = hash(output);
  }
  await fs.writeFile(path.join(root, 'callouts.json'), JSON.stringify(manifest, null, 2) + '\n');
  console.log(`Rendered ${manifest.figures.length} annotated screenshots.`);
}
main().catch(error => { console.error(error); process.exitCode = 1; });
