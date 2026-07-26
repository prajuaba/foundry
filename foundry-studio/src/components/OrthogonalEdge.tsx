import React from 'react';
import { BaseEdge, getSmoothStepPath, type EdgeProps } from '@xyflow/react';

export const OrthogonalEdge: React.FC<EdgeProps> = ({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, selected, data }) => {
  const [edgePath] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
    borderRadius: 0, // Sharp 90-degree corners
  });

  const markerEnd = data?.relationshipType || data?.type;
  
  let markerUrl = '';
  switch (markerEnd) {
    case 'Inheritance':
      markerUrl = 'url(#uml-inheritance)';
      break;
    case 'Composition':
      markerUrl = 'url(#uml-composition)';
      break;
    case 'Association':
      markerUrl = 'url(#uml-association)';
      break;
    default:
      markerUrl = 'url(#uml-association)';
  }

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        markerEnd={markerUrl}
        style={{ 
          strokeWidth: selected ? 3 : 2, 
          stroke: selected ? '#38bdf8' : '#475569',
          transition: 'stroke 0.15s, stroke-width 0.15s'
        }}
      />
      <path
        d={edgePath}
        fill="none"
        stroke="transparent"
        strokeWidth={15}
        className="react-flow__edge-interaction"
        style={{ cursor: 'pointer' }}
      />
    </>
  );
};