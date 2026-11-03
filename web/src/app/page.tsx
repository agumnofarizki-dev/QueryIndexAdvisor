"use client";

import dynamic from "next/dynamic";

// Client-only: Monaco, Web Workers and localStorage-backed state have no server rendering.
const Workspace = dynamic(() => import("@/components/Workspace").then((m) => m.Workspace), { ssr: false });

export default function Home() {
  return <Workspace />;
}
