# FastIdempotency
**Idempotency** solves this: the client sends a unique `Idempotency-Key` header with every request. The server guarantees that no matter how many retries come in with the same key, the operation executes **exactly once**, and all retries get back the **same cached response**.
