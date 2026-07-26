import { ReactFlowProvider } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { StudioWorkspace } from './components/StudioWorkspace';

function App() {
  return (
    <ReactFlowProvider>
      <StudioWorkspace />
    </ReactFlowProvider>
  );
}

export default App;
