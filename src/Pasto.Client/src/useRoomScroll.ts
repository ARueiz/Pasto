import { useCallback, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent } from 'react';
import type { Snapshot } from './api';

const bottomTolerance = 2;
const scrollKeys = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ']);

export function useRoomScroll(snapshot: Snapshot | undefined, searching: boolean, jumpThreshold = 80) {
  const list = useRef<HTMLElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const displayedVersion = useRef<bigint | null>(null);
  const sendTarget = useRef<bigint | null>(null);
  const isSearching = useRef(searching);
  const following = useRef(true);
  const smoothDestination = useRef<number | null>(null);
  const jumpVisible = useRef(false);
  const [showJump, setShowJump] = useState(false);
  const [readingSince, setReadingSince] = useState<bigint | null>(null);

  const measure = useCallback(() => {
    const element = list.current;
    if (!element || isSearching.current) return;
    const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
    const atBottom = distance <= bottomTolerance;
    if (atBottom) smoothDestination.current = null;
    if (smoothDestination.current === null && atBottom !== following.current) {
      following.current = atBottom;
      setReadingSince(atBottom ? null : displayedVersion.current);
    }

    const show = distance > jumpThreshold;
    if (show !== jumpVisible.current) {
      jumpVisible.current = show;
      setShowJump(show);
    }
  }, [jumpThreshold]);

  const scrollToBottom = useCallback((behavior?: ScrollBehavior) => {
    const element = list.current;
    if (!element) return;
    const motion = behavior ?? (smoothDestination.current === null ? 'instant' : 'smooth');
    const bottom = Math.max(0, element.scrollHeight - element.clientHeight);
    if (motion === 'smooth' && bottom === smoothDestination.current) return;
    smoothDestination.current = motion === 'smooth' ? bottom : null;
    element.scrollTo({ top: bottom, behavior: motion });
    measure();
  }, [measure]);

  const interruptJump = useCallback(() => {
    const element = list.current;
    if (!element || smoothDestination.current === null) return;
    smoothDestination.current = null;
    element.scrollTo({ top: element.scrollTop, behavior: 'instant' });
    measure();
  }, [measure]);

  useLayoutEffect(() => {
    const resumed = isSearching.current && !searching;
    isSearching.current = searching;
    if (!snapshot) return;
    const first = displayedVersion.current === null;
    const version = BigInt(snapshot.snapshotVersion);
    displayedVersion.current = version;
    if (searching) {
      sendTarget.current = null;
      interruptJump();
      return;
    }

    const reachedTarget = sendTarget.current !== null && version >= sendTarget.current;
    if (reachedTarget) sendTarget.current = null;
    // 使用這次快照更新前的位置，避免新訊息增加高度後把底部使用者誤判成離開。
    if (first || resumed || reachedTarget || following.current) scrollToBottom();
    else measure();
  }, [snapshot, searching, scrollToBottom, measure, interruptJump]);

  useLayoutEffect(() => {
    const observer = new ResizeObserver(() => {
      if (isSearching.current) return;
      if (following.current) scrollToBottom();
      else measure();
    });
    if (list.current) observer.observe(list.current);
    if (content.current) observer.observe(content.current);
    return () => observer.disconnect();
  }, [scrollToBottom, measure]);

  const unreadCount = useMemo(() => {
    if (!snapshot || readingSince === null) return 0;
    return snapshot.items.filter(item => BigInt(item.createdVersion) > readingSince).length;
  }, [snapshot, readingSince]);

  function scrollAfterSend(committedVersion: string) {
    if (isSearching.current) return;
    const version = BigInt(committedVersion);
    if (displayedVersion.current !== null && displayedVersion.current >= version) {
      sendTarget.current = null;
      scrollToBottom('instant');
    } else {
      sendTarget.current = version;
    }
  }

  function jumpToLatest() {
    following.current = true;
    setReadingSince(null);
    list.current?.focus({ preventScroll: true });
    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    scrollToBottom(reduceMotion ? 'instant' : 'smooth');
  }

  function onScrollKey(event: KeyboardEvent<HTMLElement>) {
    if (scrollKeys.has(event.key)) interruptJump();
  }

  return {
    list, content, showJump: !searching && showJump, unreadCount,
    onScroll: measure, onScrollKey, interruptJump, scrollAfterSend, jumpToLatest,
  };
}
