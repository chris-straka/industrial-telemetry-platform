import { StrictMode } from 'react'
import { ModuleRegistry, AllCommunityModule } from 'ag-grid-community'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

// Required in AG Grid v35+ to prevent tree-shaking from breaking the grid
ModuleRegistry.registerModules([AllCommunityModule])

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
