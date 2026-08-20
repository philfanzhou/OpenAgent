import { onBeforeUnmount, ref, watch, type Ref } from 'vue'

/**
 * Panel layout controller — makes the sidebar and context panel
 * hideable (collapse) and draggable (resize), with persistence.
 *
 * Widths are written to CSS variables on :root so the layout cascades;
 * the collapsed state is exposed as refs for the host to bind classes.
 */
const STORAGE_KEY = 'openagent.ui.panelLayout.v3'

interface PanelLayoutState {
  sidebarWidth: number
  sidebarCollapsed: boolean
  contextWidth: number
  contextCollapsed: boolean
}

const DEFAULTS: PanelLayoutState = {
  sidebarWidth: 240,
  sidebarCollapsed: false,
  contextWidth: 244,
  contextCollapsed: false,
}

const MIN_SIDEBAR = 210
const MAX_SIDEBAR = 380
const MIN_CONTEXT = 200
const MAX_CONTEXT = 360

function loadState(): PanelLayoutState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw) return { ...DEFAULTS, ...JSON.parse(raw) }
  } catch {
    /* ignore malformed storage */
  }
  return { ...DEFAULTS }
}

export interface PanelLayout {
  sidebarWidth: Ref<number>
  sidebarCollapsed: Ref<boolean>
  contextWidth: Ref<number>
  contextCollapsed: Ref<boolean>
  toggleSidebar: () => void
  toggleContext: () => void
  startSidebarResize: (event: PointerEvent) => void
  startContextResize: (event: PointerEvent) => void
}

export function usePanelLayout(): PanelLayout {
  const initial = loadState()
  const sidebarWidth = ref(initial.sidebarWidth)
  const sidebarCollapsed = ref(initial.sidebarCollapsed)
  const contextWidth = ref(initial.contextWidth)
  const contextCollapsed = ref(initial.contextCollapsed)

  function persist(): void {
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        sidebarWidth: sidebarWidth.value,
        sidebarCollapsed: sidebarCollapsed.value,
        contextWidth: contextWidth.value,
        contextCollapsed: contextCollapsed.value,
      }),
    )
  }

  function applyVars(): void {
    const root = document.documentElement
    root.style.setProperty(
      '--workspace-sidebar',
      `${sidebarCollapsed.value ? 0 : sidebarWidth.value}px`,
    )
    root.style.setProperty(
      '--workspace-context',
      `${contextCollapsed.value ? 0 : contextWidth.value}px`,
    )
  }

  function toggleSidebar(): void {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  function toggleContext(): void {
    contextCollapsed.value = !contextCollapsed.value
  }

  function startSidebarResize(event: PointerEvent): void {
    if (sidebarCollapsed.value) return
    event.preventDefault()
    const startX = event.clientX
    const startWidth = sidebarWidth.value

    function onMove(moveEvent: PointerEvent): void {
      const delta = moveEvent.clientX - startX
      sidebarWidth.value = Math.min(MAX_SIDEBAR, Math.max(MIN_SIDEBAR, startWidth + delta))
    }
    function onUp(): void {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      document.body.style.cursor = ''
      document.body.style.userSelect = ''
      persist()
    }

    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'
  }

  function startContextResize(event: PointerEvent): void {
    if (contextCollapsed.value) return
    event.preventDefault()
    const startX = event.clientX
    const startWidth = contextWidth.value

    function onMove(moveEvent: PointerEvent): void {
      // Dragging the left edge leftward grows the panel.
      const delta = startX - moveEvent.clientX
      contextWidth.value = Math.min(MAX_CONTEXT, Math.max(MIN_CONTEXT, startWidth + delta))
    }
    function onUp(): void {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      document.body.style.cursor = ''
      document.body.style.userSelect = ''
      persist()
    }

    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'
  }

  watch([sidebarWidth, sidebarCollapsed, contextWidth, contextCollapsed], applyVars, {
    immediate: true,
  })
  watch([sidebarCollapsed, contextCollapsed], persist)

  onBeforeUnmount(() => {
    document.body.style.cursor = ''
    document.body.style.userSelect = ''
  })

  return {
    sidebarWidth,
    sidebarCollapsed,
    contextWidth,
    contextCollapsed,
    toggleSidebar,
    toggleContext,
    startSidebarResize,
    startContextResize,
  }
}
