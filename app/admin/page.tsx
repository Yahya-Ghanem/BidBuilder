"use client"

import { useState, useEffect } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Plus, Pencil, Trash2, KeyRound, ShieldCheck, Users as UsersIcon } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import { usePermissions } from "@/lib/permissions"
import type { AdminUser, AdminGroup, ModuleRef, GroupPerm } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Button, Input, Badge } from "@/components/ui"
import { Modal, Field, Select } from "@/components/form"

/** Standard right-aligned Cancel/submit row inside a Modal. The submit button is
 *  associated to the form by id (the Modal footer renders outside the form). */
function FormActions({ formId, busy, label, onCancel }: { formId: string; busy?: boolean; label: string; onCancel: () => void }) {
  return (
    <div className="mt-4 flex justify-end gap-2">
      <Button type="button" variant="outline" onClick={onCancel}>Cancel</Button>
      <Button type="submit" form={formId} disabled={busy}>{busy ? "Saving…" : label}</Button>
    </div>
  )
}

const ROLES = ["TenantUser", "TenantAdmin"]

export default function AdminPage() {
  return (
    <AppShell title="Users & Teams">
      <AdminBody />
    </AppShell>
  )
}

function AdminBody() {
  const { isAdmin, isLoading } = usePermissions()
  if (isLoading) return <p className="text-slate-400">Loading…</p>
  if (!isAdmin) return <p className="text-rose-600">Only a tenant admin can manage users and teams.</p>
  return (
    <div className="max-w-4xl space-y-6">
      <UsersCard />
      <GroupsCard />
    </div>
  )
}

// ── Users ─────────────────────────────────────────────────────────────────────
function UsersCard() {
  const qc = useQueryClient()
  const { user: me } = useAuth()
  const { data: users } = useQuery({ queryKey: ["admin-users"], queryFn: () => fetchApi<AdminUser[]>("/api/admin/users") })
  const { data: groups } = useQuery({ queryKey: ["admin-groups"], queryFn: () => fetchApi<AdminGroup[]>("/api/admin/groups") })
  const [editing, setEditing] = useState<AdminUser | null>(null)
  const [creating, setCreating] = useState(false)
  const [resetting, setResetting] = useState<AdminUser | null>(null)

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/admin/users/${id}`, { method: "DELETE" }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin-users"] }); qc.invalidateQueries({ queryKey: ["admin-groups"] }); toast.success("User deleted") },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="space-y-4 p-5">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><UsersIcon className="h-4 w-4" /> Users</h3>
          <p className="text-xs text-slate-400">Accounts that can sign in. Assign teams to control project access &amp; module permissions.</p>
        </div>
        <Button variant="outline" className="h-9" onClick={() => setCreating(true)}><Plus className="h-4 w-4" /> New user</Button>
      </div>

      <table className="w-full text-sm">
        <thead className="text-left text-xs text-slate-500">
          <tr><th className="py-1">Name</th><th className="py-1">Email</th><th className="py-1">Role</th><th className="py-1">Teams</th><th className="py-1">Status</th><th /></tr>
        </thead>
        <tbody>
          {!users && <tr><td colSpan={6} className="py-2 text-slate-400">Loading…</td></tr>}
          {users?.map((u) => (
            <tr key={u.id} className="border-t border-[var(--border)]">
              <td className="py-2 font-medium text-slate-800">{u.name}{me?.id === u.id && <span className="ml-1 text-xs text-slate-400">(you)</span>}</td>
              <td className="py-2 text-slate-600">{u.email}</td>
              <td className="py-2">{u.role === "TenantAdmin" ? <Badge className="bg-indigo-100 text-indigo-700">Admin</Badge> : <span className="text-xs text-slate-500">User</span>}</td>
              <td className="py-2 text-xs text-slate-500">{u.groups.length === 0 ? "—" : u.groups.map((g) => g.code).join(", ")}</td>
              <td className="py-2">{u.isActive ? <Badge className="bg-emerald-100 text-emerald-700">Active</Badge> : <Badge className="bg-slate-100 text-slate-500">Inactive</Badge>}</td>
              <td className="py-2 text-right">
                <div className="flex justify-end gap-1">
                  <Button variant="outline" className="h-7 px-2 text-xs" onClick={() => setEditing(u)}><Pencil className="h-3.5 w-3.5" /></Button>
                  <Button variant="outline" className="h-7 px-2 text-xs" onClick={() => setResetting(u)}><KeyRound className="h-3.5 w-3.5" /></Button>
                  <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={me?.id === u.id || del.isPending}
                    onClick={() => { if (confirm(`Delete user "${u.name}"? This cannot be undone.`)) del.mutate(u.id) }}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {(creating || editing) && (
        <UserModal user={editing} groups={groups ?? []} onClose={() => { setCreating(false); setEditing(null) }} />
      )}
      {resetting && <ResetPasswordModal user={resetting} onClose={() => setResetting(null)} />}
    </Card>
  )
}

function UserModal({ user, groups, onClose }: { user: AdminUser | null; groups: AdminGroup[]; onClose: () => void }) {
  const qc = useQueryClient()
  const editing = user !== null
  const [name, setName] = useState(user?.name ?? "")
  const [email, setEmail] = useState(user?.email ?? "")
  const [password, setPassword] = useState("")
  const [role, setRole] = useState(user?.role ?? "TenantUser")
  const [isActive, setIsActive] = useState(user?.isActive ?? true)
  const [groupIds, setGroupIds] = useState<number[]>(user?.groups.map((g) => g.id) ?? [])

  const save = useMutation({
    mutationFn: () => {
      const body = editing
        ? JSON.stringify({ name, email, role, isActive, groupIds })
        : JSON.stringify({ name, email, password, role, groupIds })
      return fetchApi<AdminUser>(`/api/admin/users${editing ? `/${user!.id}` : ""}`, { method: editing ? "PUT" : "POST", body })
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin-users"] }); qc.invalidateQueries({ queryKey: ["admin-groups"] })
      toast.success(editing ? "User updated" : "User created"); onClose()
    },
    onError: (e) => toast.error((e as Error).message),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) return toast.error("Name is required.")
    if (!email.includes("@")) return toast.error("A valid email is required.")
    if (!editing && password.length < 8) return toast.error("Password must be at least 8 characters.")
    save.mutate()
  }

  function toggleGroup(id: number) {
    setGroupIds((cur) => cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id])
  }

  return (
    <Modal open onClose={onClose} title={editing ? `Edit ${user!.name}` : "New user"}>
      <form id="user-form" onSubmit={submit} className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Email"><Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
        </div>
        {!editing && (
          <Field label="Initial password (min 8 chars)"><Input type="text" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Set a temporary password" /></Field>
        )}
        <div className="grid grid-cols-2 gap-3">
          <Field label="Role">
            <Select value={role} onChange={(e) => setRole(e.target.value)}>
              {ROLES.map((r) => <option key={r} value={r}>{r === "TenantAdmin" ? "Admin" : "User"}</option>)}
            </Select>
          </Field>
          {editing && (
            <Field label="Status">
              <label className="flex h-9 items-center gap-2 text-sm">
                <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Active
              </label>
            </Field>
          )}
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Teams</span>
          {groups.length === 0 ? <p className="text-xs text-slate-400">No teams yet — create one below.</p> : (
            <div className="grid grid-cols-2 gap-1">
              {groups.map((g) => (
                <label key={g.id} className="flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-slate-50">
                  <input type="checkbox" checked={groupIds.includes(g.id)} onChange={() => toggleGroup(g.id)} />
                  <span className="font-mono text-xs text-slate-500">{g.code}</span> {g.name}
                </label>
              ))}
            </div>
          )}
        </div>
      </form>
      <FormActions formId="user-form" busy={save.isPending} label={editing ? "Save" : "Create"} onCancel={onClose} />
    </Modal>
  )
}

function ResetPasswordModal({ user, onClose }: { user: AdminUser; onClose: () => void }) {
  const [password, setPassword] = useState("")
  const reset = useMutation({
    mutationFn: () => fetchApi(`/api/admin/users/${user.id}/reset-password`, { method: "POST", body: JSON.stringify({ password }) }),
    onSuccess: () => { toast.success(`Password reset for ${user.email}`); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })
  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (password.length < 8) return toast.error("Password must be at least 8 characters.")
    reset.mutate()
  }
  return (
    <Modal open onClose={onClose} title={`Reset password — ${user.name}`}>
      <form id="reset-form" onSubmit={submit} className="space-y-3">
        <p className="text-xs text-slate-500">Set a new password for <b>{user.email}</b>. They can change it after signing in.</p>
        <Field label="New password (min 8 chars)"><Input type="text" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
      </form>
      <FormActions formId="reset-form" busy={reset.isPending} label="Reset password" onCancel={onClose} />
    </Modal>
  )
}

// ── Groups / teams ──────────────────────────────────────────────────────────
function GroupsCard() {
  const qc = useQueryClient()
  const { data: groups } = useQuery({ queryKey: ["admin-groups"], queryFn: () => fetchApi<AdminGroup[]>("/api/admin/groups") })
  const { data: modules } = useQuery({ queryKey: ["admin-modules"], queryFn: () => fetchApi<ModuleRef[]>("/api/admin/modules") })
  const [editing, setEditing] = useState<AdminGroup | null>(null)
  const [creating, setCreating] = useState(false)
  const [perms, setPerms] = useState<AdminGroup | null>(null)

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/admin/groups/${id}`, { method: "DELETE" }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin-groups"] }); toast.success("Team deleted") },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="space-y-4 p-5">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><ShieldCheck className="h-4 w-4" /> Teams &amp; permissions</h3>
          <p className="text-xs text-slate-400">A team grants its members a set of module permissions and is assigned to projects.</p>
        </div>
        <Button variant="outline" className="h-9" onClick={() => setCreating(true)}><Plus className="h-4 w-4" /> New team</Button>
      </div>

      <table className="w-full text-sm">
        <thead className="text-left text-xs text-slate-500">
          <tr><th className="py-1">Name</th><th className="py-1">Code</th><th className="py-1">Members</th><th className="py-1">Modules</th><th /></tr>
        </thead>
        <tbody>
          {!groups && <tr><td colSpan={5} className="py-2 text-slate-400">Loading…</td></tr>}
          {groups?.map((g) => (
            <tr key={g.id} className="border-t border-[var(--border)]">
              <td className="py-2 font-medium text-slate-800">{g.name}{g.isBuiltIn && <span className="ml-1 text-xs text-slate-400">built-in</span>}</td>
              <td className="py-2 font-mono text-xs">{g.code}</td>
              <td className="py-2 text-slate-600">{g.memberCount}</td>
              <td className="py-2 text-xs text-slate-500">{g.permissions.length}</td>
              <td className="py-2 text-right">
                <div className="flex justify-end gap-1">
                  <Button variant="outline" className="h-7 px-2 text-xs" onClick={() => setPerms(g)}><ShieldCheck className="h-3.5 w-3.5" /> Permissions</Button>
                  <Button variant="outline" className="h-7 px-2 text-xs" onClick={() => setEditing(g)}><Pencil className="h-3.5 w-3.5" /></Button>
                  <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={g.isBuiltIn || del.isPending}
                    onClick={() => { if (confirm(`Delete team "${g.name}"?`)) del.mutate(g.id) }}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {(creating || editing) && <GroupModal group={editing} onClose={() => { setCreating(false); setEditing(null) }} />}
      {perms && modules && <PermissionsModal group={perms} modules={modules} onClose={() => setPerms(null)} />}
    </Card>
  )
}

function GroupModal({ group, onClose }: { group: AdminGroup | null; onClose: () => void }) {
  const qc = useQueryClient()
  const editing = group !== null
  const [code, setCode] = useState(group?.code ?? "")
  const [name, setName] = useState(group?.name ?? "")
  const [description, setDescription] = useState(group?.description ?? "")

  const save = useMutation({
    mutationFn: () => fetchApi<AdminGroup>(`/api/admin/groups${editing ? `/${group!.id}` : ""}`, {
      method: editing ? "PUT" : "POST",
      body: editing ? JSON.stringify({ name, description }) : JSON.stringify({ code, name, description }),
    }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin-groups"] }); toast.success(editing ? "Team updated" : "Team created"); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })
  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!editing && !code.trim()) return toast.error("Code is required.")
    if (!name.trim()) return toast.error("Name is required.")
    save.mutate()
  }
  return (
    <Modal open onClose={onClose} title={editing ? `Edit ${group!.name}` : "New team"}>
      <form id="group-form" onSubmit={submit} className="space-y-3">
        <Field label="Code (immutable)"><Input value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} disabled={editing} placeholder="TEAM-A" maxLength={32} /></Field>
        <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Estimating Team A" /></Field>
        <Field label="Description"><Input value={description ?? ""} onChange={(e) => setDescription(e.target.value)} /></Field>
      </form>
      <FormActions formId="group-form" busy={save.isPending} label={editing ? "Save" : "Create"} onCancel={onClose} />
    </Modal>
  )
}

type PermFlag = "canView" | "canAdd" | "canEdit" | "canDelete"
const ACTIONS: { key: PermFlag; label: string }[] = [
  { key: "canView", label: "View" }, { key: "canAdd", label: "Add" }, { key: "canEdit", label: "Edit" }, { key: "canDelete", label: "Delete" },
]

function PermissionsModal({ group, modules, onClose }: { group: AdminGroup; modules: ModuleRef[]; onClose: () => void }) {
  const qc = useQueryClient()
  // Seed a full grid: one row per module, merging the group's existing flags.
  const [grid, setGrid] = useState<Record<number, GroupPerm>>(() => {
    const m: Record<number, GroupPerm> = {}
    for (const mod of modules) {
      const ex = group.permissions.find((p) => p.moduleId === mod.id)
      m[mod.id] = ex ?? { moduleId: mod.id, canView: false, canAdd: false, canEdit: false, canDelete: false }
    }
    return m
  })

  function toggle(moduleId: number, key: PermFlag) {
    setGrid((cur) => {
      const row: GroupPerm = { ...cur[moduleId], [key]: !cur[moduleId][key] }
      // Add/Edit/Delete imply View; clearing View clears the rest.
      if (key === "canView" && !row.canView) { row.canAdd = false; row.canEdit = false; row.canDelete = false }
      if (key !== "canView" && row[key]) row.canView = true
      return { ...cur, [moduleId]: row }
    })
  }

  const save = useMutation({
    mutationFn: () => fetchApi<AdminGroup>(`/api/admin/groups/${group.id}/permissions`, {
      method: "PUT", body: JSON.stringify({ permissions: Object.values(grid) }),
    }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin-groups"] }); qc.invalidateQueries({ queryKey: ["permissions"] }); toast.success("Permissions saved"); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Modal open onClose={onClose} title={`Permissions — ${group.name}`}>
      <p className="mb-3 text-xs text-slate-500">Tick what members of <b>{group.code}</b> may do per module. Add/Edit/Delete require View.</p>
      <table className="w-full text-sm">
        <thead className="text-left text-xs text-slate-500">
          <tr><th className="py-1">Module</th>{ACTIONS.map((a) => <th key={a.key} className="px-2 py-1 text-center">{a.label}</th>)}</tr>
        </thead>
        <tbody>
          {modules.map((mod) => (
            <tr key={mod.id} className="border-t border-[var(--border)]">
              <td className="py-2">{mod.name} <span className="font-mono text-xs text-slate-400">{mod.code}</span></td>
              {ACTIONS.map((a) => (
                <td key={a.key} className="px-2 py-2 text-center">
                  <input type="checkbox" checked={grid[mod.id][a.key]} onChange={() => toggle(mod.id, a.key)} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="button" disabled={save.isPending} onClick={() => save.mutate()}>{save.isPending ? "Saving…" : "Save permissions"}</Button>
      </div>
    </Modal>
  )
}
