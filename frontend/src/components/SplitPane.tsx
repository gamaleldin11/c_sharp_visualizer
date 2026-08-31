import { useState, useRef, useEffect, ReactNode } from 'react';

interface SplitPaneProps {
  split: 'vertical' | 'horizontal';
  initialSizes?: [number, number]; // percentages
  children: [ReactNode, ReactNode];
  minSizes?: [number, number]; // pixels
}

export function SplitPane({ split, initialSizes = [50, 50], minSizes = [100, 100], children }: SplitPaneProps) {
  const [sizes, setSizes] = useState(initialSizes);
  const [isDragging, setIsDragging] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!isDragging) return;

    const handleMouseMove = (e: MouseEvent | TouchEvent) => {
      if (!containerRef.current) return;
      const rect = containerRef.current.getBoundingClientRect();
      
      const clientX = 'touches' in e ? e.touches[0].clientX : e.clientX;
      const clientY = 'touches' in e ? e.touches[0].clientY : e.clientY;

      let newPercentage = 0;
      if (split === 'vertical') {
        const x = Math.max(minSizes[0], Math.min(clientX - rect.left, rect.width - minSizes[1]));
        newPercentage = (x / rect.width) * 100;
      } else {
        const y = Math.max(minSizes[0], Math.min(clientY - rect.top, rect.height - minSizes[1]));
        newPercentage = (y / rect.height) * 100;
      }
      
      setSizes([newPercentage, 100 - newPercentage]);
    };

    const handleMouseUp = () => {
      setIsDragging(false);
    };

    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
    document.addEventListener('touchmove', handleMouseMove, { passive: false });
    document.addEventListener('touchend', handleMouseUp);

    return () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
      document.removeEventListener('touchmove', handleMouseMove);
      document.removeEventListener('touchend', handleMouseUp);
    };
  }, [isDragging, split, minSizes]);

  const cursor = split === 'vertical' ? 'col-resize' : 'row-resize';

  return (
    <div 
      ref={containerRef} 
      className={`split-pane split-pane-${split}`}
      style={{
        display: 'flex',
        flexDirection: split === 'vertical' ? 'row' : 'column',
        width: '100%',
        height: '100%',
        overflow: 'hidden'
      }}
    >
      <div style={{ flex: `0 0 ${sizes[0]}%`, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {children[0]}
      </div>
      
      <div 
        className="split-resizer"
        style={{
          cursor,
          flex: '0 0 6px',
          margin: split === 'vertical' ? '0 -3px' : '-3px 0',
          zIndex: 10,
          background: 'transparent',
          position: 'relative',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center'
        }}
        onMouseDown={(e) => { e.preventDefault(); setIsDragging(true); }}
        onTouchStart={(e) => { setIsDragging(true); }}
      >
        <div className="resizer-visible" />
      </div>

      <div style={{ flex: `0 0 ${sizes[1]}%`, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {children[1]}
      </div>

      {isDragging && (
        <div 
          style={{
            position: 'absolute',
            top: 0, left: 0, right: 0, bottom: 0,
            zIndex: 9999,
            cursor
          }} 
        />
      )}
    </div>
  );
}
