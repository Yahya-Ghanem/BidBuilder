"use client"

import { AppShell } from "@/components/app-shell"
import { ProjectsDetailPane } from "@/components/projects-tree"

export default function ProjectsPage() {
  return (
    <AppShell title="Projects">
      <ProjectsDetailPane />
    </AppShell>
  )
}
