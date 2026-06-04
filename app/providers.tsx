"use client"

import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { useState, type ReactNode } from "react"
import { Toaster } from "sonner"
import { AuthProvider } from "@/lib/auth"

export function Providers({ children }: { children: ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: { queries: { staleTime: 1000 * 60 * 5, retry: 1 } },
      }),
  )
  return (
    <QueryClientProvider client={client}>
      <AuthProvider>{children}</AuthProvider>
      <Toaster richColors position="top-right" />
    </QueryClientProvider>
  )
}
