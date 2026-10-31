// Bridge between the Unity WebGL build and WharfKit.
// Unity calls window.WaxKitJs.call(method, args); every call resolves to
// { ok: true, data } or { ok: false, error: { kind, message } } and never rejects.
// WharfKit draws no HTML: its UI steps go to Unity through UnityUserInterface.

import { Canceled, Session, SessionKit } from '@wharfkit/session'
import { WalletPluginAnchor } from '@wharfkit/wallet-plugin-anchor'
import { WalletPluginCloudWallet } from '@wharfkit/wallet-plugin-cloudwallet'
import { ErrorKind, WaxKitError } from './errors'
import { createFailoverFetch } from './fetch'
import { cancelledByUser, UiAction, UiEvent, UnityUserInterface } from './ui'

const STORAGE_KEY = 'waxkit.session'

type WalletType = 'Anchor' | 'MyCloudWallet'

interface Config {
    appName: string
    chainId: string
    rpcEndpoints: string[]
}

interface SessionInfo {
    walletType: WalletType
    actor: string
    permission: string
    chainId: string
}

const ui = new UnityUserInterface()
let config: Config | undefined
let fetchWithFailover: typeof fetch | undefined
let current: { session: Session; info: SessionInfo } | undefined
let cancelCall: (() => void) | undefined
const kits = new Map<string, SessionKit>()

function isWalletType(value: unknown): value is WalletType {
    return value === 'Anchor' || value === 'MyCloudWallet'
}

function requireConfig(): Config {
    if (!config) throw new WaxKitError('Wallet', 'WaxKit is not initialized. Call WaxKit.Initialize first.')
    return config
}

// One SessionKit per wallet + chain, all sharing the Unity UI and the failover fetch.
function getKit(walletType: WalletType): SessionKit {
    const { appName, chainId, rpcEndpoints } = requireConfig()
    const key = `${walletType}:${chainId}`
    let kit = kits.get(key)
    if (!kit) {
        kit = new SessionKit(
            {
                appName,
                chains: [{ id: chainId, url: rpcEndpoints[0] }],
                ui,
                walletPlugins: [walletType === 'Anchor' ? new WalletPluginAnchor() : new WalletPluginCloudWallet()],
            },
            { fetch: fetchWithFailover }
        )
        kits.set(key, kit)
    }
    return kit
}

function readRecord(): SessionInfo | undefined {
    try {
        const raw = localStorage.getItem(STORAGE_KEY)
        return raw ? JSON.parse(raw) : undefined
    } catch {
        return undefined
    }
}

function writeRecord(info: SessionInfo | undefined) {
    try {
        if (info) localStorage.setItem(STORAGE_KEY, JSON.stringify(info))
        else localStorage.removeItem(STORAGE_KEY)
    } catch {
        // Storage can be blocked (private mode); the session still works for this page.
    }
}

function toInfo(session: Session, walletType: WalletType): SessionInfo {
    return {
        walletType,
        actor: String(session.actor),
        permission: String(session.permission),
        chainId: String(session.chain.id),
    }
}

async function exitFullscreen() {
    if (document.fullscreenElement) {
        await document.exitFullscreen().catch(() => undefined)
    }
}

// Ends the wallet step as soon as the user cancels, even if the wallet keeps waiting.
function untilCancelled<T>(promise: Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        cancelCall = () => reject(cancelledByUser())
        promise.then(resolve, reject)
    })
}

async function login(args: { walletType: WalletType }): Promise<SessionInfo> {
    const { chainId } = requireConfig()
    const { walletType } = args
    if (!isWalletType(walletType)) throw new WaxKitError('NotSupported', `Unknown wallet: ${walletType}`)
    await exitFullscreen()
    ui.anchorActive = walletType === 'Anchor'
    if (walletType === 'MyCloudWallet') {
        await ui.askContinue('My Cloud Wallet', 'Press Continue to open the My Cloud Wallet window.')
    }
    const kit = getKit(walletType)
    const { session } = await untilCancelled(kit.login({ chain: chainId, walletPlugin: kit.walletPlugins[0].id }))
    const info = toInfo(session, walletType)
    current = { session, info }
    writeRecord(info)
    return info
}

async function restore(): Promise<SessionInfo | null> {
    const { chainId } = requireConfig()
    const record = readRecord()
    if (!record || record.chainId !== chainId || !isWalletType(record.walletType)) {
        if (record) writeRecord(undefined)
        return null
    }
    let session: Session | undefined
    try {
        session = await getKit(record.walletType).restore({
            chain: chainId,
            actor: record.actor,
            permission: record.permission,
        })
    } catch {
        session = undefined
    }
    if (!session) {
        writeRecord(undefined)
        return null
    }
    const info = toInfo(session, record.walletType)
    current = { session, info }
    return info
}

async function logout(): Promise<null> {
    const active = current
    current = undefined
    writeRecord(undefined)
    if (active) await getKit(active.info.walletType).logout(active.session)
    return null
}

async function transact(args: { actions: unknown[] }): Promise<{ transactionId: string }> {
    if (!current) throw new WaxKitError('NotLoggedIn', 'Not logged in.')
    await exitFullscreen()
    ui.anchorActive = current.info.walletType === 'Anchor'
    if (current.info.walletType === 'MyCloudWallet') {
        await ui.askContinue('Sign transaction', 'Press Continue to open My Cloud Wallet and sign.')
    }
    const result = await untilCancelled(current.session.transact({ actions: args.actions as never }))
    const id = result.resolved?.transaction.id ?? result.response?.transaction_id
    return { transactionId: String(id ?? '') }
}

function toError(error: unknown): { kind: ErrorKind; message: string } {
    if (error instanceof WaxKitError) return { kind: error.kind, message: error.message }
    const message = (error instanceof Error ? error.message : String(error)) || 'Unknown wallet error'
    if (error instanceof Canceled || /cancel|closed|reject|declin|denied/i.test(message)) {
        return { kind: 'Cancelled', message }
    }
    if (/expired|timeout|timed out/i.test(message)) return { kind: 'Timeout', message }
    if (/failed to fetch|network|load failed/i.test(message)) return { kind: 'Network', message }
    return { kind: 'Wallet', message }
}

const methods: Record<string, (args: any) => Promise<unknown>> = { login, restore, logout, transact }

const WaxKitJs = {
    init(next: Config, onUiEvent: (json: string) => void) {
        config = next
        fetchWithFailover = createFailoverFetch(next.rpcEndpoints)
        ui.send = (event: UiEvent) => onUiEvent(JSON.stringify(event))
        current = undefined
        kits.clear()
    },
    async call(method: string, args: unknown) {
        ui.shown = false
        try {
            const handler = methods[method]
            if (!handler) throw new WaxKitError('NotSupported', `Unknown method: ${method}`)
            const data = (await handler(args)) ?? null
            if (ui.shown) ui.close()
            return { ok: true, data }
        } catch (e) {
            const error = toError(e)
            if (ui.shown) {
                if (error.kind === 'Cancelled') ui.close()
                else ui.showError(error.message)
            }
            return { ok: false, error }
        } finally {
            cancelCall = undefined
        }
    },
    /** A user answer from Unity: 'continue', 'openAnchor' or 'cancel'. */
    answer(promptId: number, action: UiAction) {
        if (action === 'cancel') cancelCall?.()
        ui.answer(promptId, action)
    },
    /** Runs the action inside the next browser pointerup / touchend. */
    arm(action: UiAction) {
        ui.arm(action)
    },
}

;(window as any).WaxKitJs = WaxKitJs
