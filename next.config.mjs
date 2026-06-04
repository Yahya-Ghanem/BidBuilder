/** @type {import('next').NextConfig} */
const nextConfig = {
  // Self-contained server.js for a small production Docker image.
  output: "standalone",
  typescript: { ignoreBuildErrors: false },
  images: { unoptimized: true },
}

export default nextConfig
