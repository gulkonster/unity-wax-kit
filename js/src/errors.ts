export type ErrorKind = 'Cancelled' | 'Timeout' | 'Wallet' | 'Network' | 'NotLoggedIn' | 'WrongChain' | 'NotSupported'

export class WaxKitError extends Error {
    constructor(public kind: ErrorKind, message: string) {
        super(message)
    }
}
