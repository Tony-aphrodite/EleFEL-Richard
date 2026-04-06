import Database from 'better-sqlite3'
import path from 'path'
import fs from 'fs'

const DB_PATH = process.env.DATABASE_PATH || path.join(process.cwd(), 'data', 'licenses.db')

function getDb() {
  const dir = path.dirname(DB_PATH)
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true })
  }

  const db = new Database(DB_PATH)
  db.pragma('journal_mode = WAL')

  db.exec(`
    CREATE TABLE IF NOT EXISTS licenses (
      id          INTEGER PRIMARY KEY AUTOINCREMENT,
      license_key TEXT    NOT NULL UNIQUE,
      client_name TEXT    NOT NULL,
      client_email TEXT   DEFAULT '',
      client_phone TEXT   DEFAULT '',
      status      TEXT    NOT NULL DEFAULT 'active',
      created_at  TEXT    NOT NULL,
      expires_at  TEXT    NOT NULL,
      notes       TEXT    DEFAULT ''
    )
  `)

  return db
}

export interface License {
  id: number
  license_key: string
  client_name: string
  client_email: string
  client_phone: string
  status: 'active' | 'inactive'
  created_at: string
  expires_at: string
  notes: string
}

export function getAllLicenses(): License[] {
  const db = getDb()
  return db.prepare('SELECT * FROM licenses ORDER BY created_at DESC').all() as License[]
}

export function getLicenseByKey(key: string): License | undefined {
  const db = getDb()
  return db.prepare('SELECT * FROM licenses WHERE license_key = ?').get(key) as License | undefined
}

export function getLicenseById(id: number): License | undefined {
  const db = getDb()
  return db.prepare('SELECT * FROM licenses WHERE id = ?').get(id) as License | undefined
}

export function createLicense(data: {
  client_name: string
  client_email?: string
  client_phone?: string
  expires_at: string
  notes?: string
}): License {
  const db = getDb()
  const key = generateLicenseKey()
  const now = new Date().toISOString()

  db.prepare(`
    INSERT INTO licenses (license_key, client_name, client_email, client_phone, status, created_at, expires_at, notes)
    VALUES (?, ?, ?, ?, 'active', ?, ?, ?)
  `).run(key, data.client_name, data.client_email || '', data.client_phone || '', now, data.expires_at, data.notes || '')

  return getLicenseByKey(key)!
}

export function updateLicenseStatus(id: number, status: 'active' | 'inactive') {
  const db = getDb()
  db.prepare('UPDATE licenses SET status = ? WHERE id = ?').run(status, id)
}

export function renewLicense(id: number, days: number = 30) {
  const db = getDb()
  const license = getLicenseById(id)
  if (!license) return

  const current = new Date(license.expires_at)
  const base = current > new Date() ? current : new Date()
  base.setDate(base.getDate() + days)

  db.prepare('UPDATE licenses SET expires_at = ?, status = ? WHERE id = ?')
    .run(base.toISOString(), 'active', id)
}

export function deleteLicense(id: number) {
  const db = getDb()
  db.prepare('DELETE FROM licenses WHERE id = ?').run(id)
}

function generateLicenseKey(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
  const segment = () =>
    Array.from({ length: 4 }, () => chars[Math.floor(Math.random() * chars.length)]).join('')
  return `EFEL-${segment()}-${segment()}-${segment()}`
}
