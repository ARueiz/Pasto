import { useEffect, useState } from 'react';
import { isOverloaded } from './api';
import type { ApiError } from './api';

export function useOverloadWait(error: unknown) {
  const [expired, setExpired] = useState<ApiError>();
  useEffect(() => {
    if (!isOverloaded(error)) return;
    const timer = window.setTimeout(() => setExpired(error), Math.max(0, error.retryAt - performance.now()));
    return () => window.clearTimeout(timer);
  }, [error]);
  return isOverloaded(error) && error !== expired;
}
