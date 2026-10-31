// fetch for WharfKit that fails over between the network's RPC endpoints.
// Requests to other hosts (wallet services) pass straight through.

import { WaxKitError } from './errors'

const TIMEOUT_MS = 10_000
const BACKOFF_MS = 300

export function createFailoverFetch(endpoints: string[]): typeof fetch {
    const bases = endpoints.map((url) => url.replace(/\/+$/, ''))
    let preferred = 0

    return async (input, init) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
        const base = bases.find((b) => url.startsWith(b + '/'))
        if (!base) return fetch(input, init)

        const path = url.slice(base.length)
        let lastError = ''
        for (let attempt = 0; attempt < bases.length; attempt++) {
            if (attempt > 0) await new Promise((resolve) => setTimeout(resolve, BACKOFF_MS * attempt))
            const index = (preferred + attempt) % bases.length
            const target = bases[index] + path
            try {
                const response = await fetch(target, { ...init, signal: AbortSignal.timeout(TIMEOUT_MS) })
                if (response.status !== 429 && response.status < 500) {
                    preferred = index
                    return response
                }
                lastError = `${target}: HTTP ${response.status}`
            } catch (error) {
                lastError = `${target}: ${error instanceof Error ? error.message : String(error)}`
            }
        }
        throw new WaxKitError('Network', `Network error: all RPC endpoints failed. Last error: ${lastError}`)
    }
}
