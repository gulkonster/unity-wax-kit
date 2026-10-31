// WharfKit UserInterface that draws nothing: every UI step goes to Unity as a JSON event,
// and Unity answers with continue / openAnchor / cancel.

import {
    AbstractUserInterface,
    Canceled,
    cancelable,
    Cancelable,
    LoginContext,
    PromptArgs,
    PromptElement,
    PromptResponse,
    UserInterfaceLoginResponse,
} from '@wharfkit/session'
import qrcode from 'qrcode-generator'

export type UiAction = 'continue' | 'openAnchor' | 'cancel'

type UiKind = 'AnchorSign' | 'Continue' | 'Waiting' | 'Error'

export interface UiEvent {
    type: 'show' | 'close'
    id?: number
    kind?: UiKind
    title?: string
    text?: string
    qr?: { size: number; modules: string } | null
    canOpenAnchor?: boolean
    /** Unix time in milliseconds. */
    expiresAt?: number | null
}

interface ActivePrompt {
    id: number
    actions: Partial<Record<UiAction, () => void>>
}

export class UnityUserInterface extends AbstractUserInterface {
    /** Set by the bridge; sends one event to Unity. */
    send: (event: UiEvent) => void = () => undefined
    /** Prompts with a QR, a link or a button belong to Anchor, unless this is false. */
    anchorActive = false
    /** True once this call showed anything, so a failure can be shown as an error screen. */
    shown = false

    private nextId = 0
    private active: ActivePrompt | undefined
    private armed: UiAction | undefined

    /** Runs a user answer for the prompt on screen. Answers for older prompts are ignored. */
    answer(promptId: number, action: UiAction) {
        if (this.active?.id === promptId) this.active.actions[action]?.()
    }

    /** Runs the action inside the next browser pointerup / touchend, so wallets may open windows and links. */
    arm(action: UiAction) {
        if (this.armed === undefined) {
            const run = () => {
                window.removeEventListener('pointerup', run, true)
                window.removeEventListener('touchend', run, true)
                const armed = this.armed
                this.armed = undefined
                if (armed) this.active?.actions[armed]?.()
            }
            window.addEventListener('pointerup', run, true)
            window.addEventListener('touchend', run, true)
        }
        this.armed = action
    }

    /** Asks for a real click before a wallet opens its window. */
    askContinue(title: string, text: string): Promise<void> {
        return new Promise((resolve, reject) => {
            this.show('Continue', { title, text }, { continue: resolve, cancel: () => reject(cancelledByUser()) })
        })
    }

    showError(message: string) {
        this.active = undefined
        this.send({ type: 'show', id: ++this.nextId, kind: 'Error', text: message })
    }

    close() {
        this.active = undefined
        this.send({ type: 'close' })
    }

    prompt(args: PromptArgs): Cancelable<PromptResponse> {
        let fail: (reason: unknown) => void = () => undefined
        const promise = new Promise<PromptResponse>((resolve, reject) => {
            fail = reject
            const qr = find(args.elements, 'qr')
            const link = find(args.elements, 'link')
            const button = find(args.elements, 'button')
            const countdown = find(args.elements, 'countdown')

            const buttonClick = (button?.data as { onClick?: () => void } | undefined)?.onClick
            const linkHref = (link?.data as { href?: string } | undefined)?.href
            const cancel = () => reject(cancelledByUser())

            if (this.anchorActive && (qr || link || buttonClick)) {
                const openAnchor = linkHref ? () => (window.location.href = linkHref) : buttonClick
                this.show(
                    'AnchorSign',
                    {
                        title: args.title,
                        text: args.body,
                        qr: qr ? toMatrix(String(qr.data)) : null,
                        canOpenAnchor: !!openAnchor,
                        expiresAt: countdownEnd(countdown),
                    },
                    { openAnchor, cancel }
                )
            } else if (buttonClick) {
                this.show('Continue', { title: args.title, text: args.body }, { continue: buttonClick, cancel })
            } else {
                this.show('Waiting', { title: args.title, text: args.body }, { cancel })
            }
        })
        const id = this.nextId
        return cancelable(promise, (canceled) => {
            if (this.active?.id === id) this.close()
            fail(canceled)
        })
    }

    status(message: string) {
        if (!this.active) this.show('Waiting', { text: message }, {})
    }

    async login(context: LoginContext): Promise<UserInterfaceLoginResponse> {
        return { chainId: context.chain?.id ?? context.chains[0].id, walletPluginIndex: 0 }
    }

    async onError() {}
    async onAccountCreate(): Promise<never> {
        throw new Error('Account creation is not supported.')
    }
    async onAccountCreateComplete() {}
    async onLogin() {}
    async onLoginComplete() {
        this.close()
    }
    async onTransact() {}
    async onTransactComplete() {
        this.close()
    }
    async onSign() {}
    async onSignComplete() {}
    async onBroadcast() {}
    async onBroadcastComplete() {}

    translate(key: string, options?: { default?: string }): string {
        return options?.default ?? key
    }
    addTranslations() {}

    private show(kind: UiKind, content: Omit<UiEvent, 'type' | 'id' | 'kind'>, actions: ActivePrompt['actions']) {
        this.active = { id: ++this.nextId, actions }
        this.shown = true
        this.send({ type: 'show', id: this.active.id, kind, ...content })
    }
}

export function cancelledByUser() {
    return new Canceled('Cancelled by the user.')
}

function find(elements: PromptElement[], type: PromptElement['type']) {
    return elements.find((element) => element.type === type)
}

// Countdown data is an ISO date (Cloud Wallet) or { end } (Anchor).
function countdownEnd(element: PromptElement | undefined): number | null {
    const data = element?.data as string | { end?: string } | undefined
    const end = Date.parse(typeof data === 'string' ? data : data?.end ?? '')
    return Number.isNaN(end) ? null : end
}

// Module matrix row by row, '1' = dark, top-left first.
function toMatrix(text: string) {
    const qr = qrcode(0, 'M')
    qr.addData(text)
    qr.make()
    const size = qr.getModuleCount()
    let modules = ''
    for (let row = 0; row < size; row++) {
        for (let col = 0; col < size; col++) modules += qr.isDark(row, col) ? '1' : '0'
    }
    return { size, modules }
}
