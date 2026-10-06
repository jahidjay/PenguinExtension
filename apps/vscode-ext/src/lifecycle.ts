// Invalidate synchronously; serialize stop/start so an older start cannot win a race.
export class OrderedLifecycle {
    private generation = 0;
    private closed = false;
    private chain = Promise.resolve();
    constructor(private readonly stop: () => Promise<void>, private readonly start: (current: () => boolean) => Promise<void>, private readonly error: (error: unknown) => void) {}
    restart(): Promise<void> {
        if (this.closed) { return this.chain; }
        const generation = ++this.generation;
        this.chain = this.chain.then(async () => {
            await this.stop();
            if (!this.closed && generation === this.generation) { await this.start(() => !this.closed && generation === this.generation); }
        }).catch(this.error);
        return this.chain;
    }
    async dispose(): Promise<void> {
        this.closed = true; ++this.generation;
        await this.chain;
        await this.stop();
    }
}
