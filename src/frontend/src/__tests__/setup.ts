// Registers a happy-dom global environment so React components can render under bun test.
import { GlobalRegistrator } from '@happy-dom/global-registrator'

GlobalRegistrator.register()

// Let React Testing Library's internal act() run without environment warnings
;(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true
