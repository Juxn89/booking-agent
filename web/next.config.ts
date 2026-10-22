import type { NextConfig } from "next";

// The browser only ever talks to this app; /api is forwarded to the .NET API, so both share one origin:
// no CORS, and no cookie or token for a cross-site request to ride on.
const apiUrl = process.env.API_URL ?? "http://localhost:5120";

const nextConfig: NextConfig = {
	output: "standalone",
	poweredByHeader: false,
	async rewrites() {
		return [{ source: "/api/:path*", destination: `${apiUrl}/:path*` }];
	},
	async headers() {
		return [
			{
				source: "/:path*",
				headers: [
					{ key: "X-Content-Type-Options", value: "nosniff" },
					{ key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
					{ key: "X-Frame-Options", value: "DENY" },
				],
			},
		];
	},
};

export default nextConfig;
