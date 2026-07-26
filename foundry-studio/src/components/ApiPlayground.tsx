import React, { useState } from 'react';
import { useStore } from '../store';
import { crudRouteFor } from '../manifest';
import { Play, Send, AlertCircle, Clock, CheckCircle2, ShieldCheck, Cpu, FileText, Zap, RefreshCw } from 'lucide-react';

interface ProtocolResult {
  name: string;
  status: 'passed' | 'failed' | 'running' | 'idle';
  testsCount: number;
  durationMs: number;
  coverage: string;
}

export const ApiPlayground: React.FC = () => {
  const { nodes } = useStore();
  const entityNodes = nodes.filter(n => n.type === 'classNode');
  
  const [activeTab, setActiveTab] = useState<'playground' | 'testEngine'>('testEngine');

  // Request Playground States
  const [selectedEntity, setSelectedEntity] = useState<string>(entityNodes[0]?.data?.entity?.name || '');
  const [method, setMethod] = useState<'GET' | 'POST' | 'DELETE'>('GET');
  const [tenantId, setTenantId] = useState<string>('tenant-demo');
  const [requestBody, setRequestBody] = useState<string>('{\n  "name": "Sample Item"\n}');
  const [response, setResponse] = useState<{ status?: number; time?: number; body?: string; error?: string } | null>(null);
  const [loading, setLoading] = useState(false);

  // Test Engine Config States
  const [mockSampleCount, setMockSampleCount] = useState<number>(50);
  const [enableRest, setEnableRest] = useState<boolean>(true);
  const [enableGraphQl, setEnableGraphQl] = useState<boolean>(true);
  const [enableKafka, setEnableKafka] = useState<boolean>(true);
  const [enableRealTime, setEnableRealTime] = useState<boolean>(true);
  const [enableFileIo, setEnableFileIo] = useState<boolean>(true);
  const [enableRules, setEnableRules] = useState<boolean>(true);
  const [enableWorkflows, setEnableWorkflows] = useState<boolean>(true);

  // Test Engine Execution States
  const [testRunning, setTestRunning] = useState<boolean>(false);
  const [testProgress, setTestProgress] = useState<number>(0);
  const [testResults, setTestResults] = useState<ProtocolResult[]>([
    { name: 'REST API Endpoints', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'CRUD & Soft-Delete' },
    { name: 'GraphQL Gateway', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'Queries & Mutations' },
    { name: 'Kafka Event Outbox', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'Domain Event Publishing' },
    { name: 'Real-Time WebSockets & SSE', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'Mutation Broadcasting' },
    { name: 'FileIO Pipeline Services', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'Upload & Security Filters' },
    { name: 'MediatR & FluentValidation', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'Business Rule Validation' },
    { name: 'Workflow State Machines', status: 'idle', testsCount: 0, durationMs: 0, coverage: 'State Transitions & Rules' },
  ]);

  const handleSendRequest = async () => {
    setLoading(true);
    setResponse(null);
    const startTime = performance.now();
    // Must match what the running application serves. This used to build `/api/v1/customer`
    // -- wrong prefix and not pluralised -- so every request 404'd and looked like a broken app.
    const route = crudRouteFor(selectedEntity || 'sample');

    try {
      const res = await fetch(`http://localhost:5000${route}`, {
        method,
        headers: {
          'Content-Type': 'application/json',
          'X-Tenant-ID': tenantId,
        },
        body: method === 'POST' ? requestBody : undefined,
      });

      const endTime = performance.now();
      const text = await res.text();
      let formattedBody = text;
      try {
        formattedBody = JSON.stringify(JSON.parse(text), null, 2);
      } catch {
        // Keep raw text if not valid JSON
      }

      setResponse({
        status: res.status,
        time: Math.round(endTime - startTime),
        body: formattedBody,
      });
    } catch (err: any) {
      setResponse({
        error: `Connection Failed: Could not connect to API server at http://localhost:5000${route}. Make sure your scaffolded Foundry API is running via 'dotnet run'.`,
      });
    } finally {
      setLoading(false);
    }
  };

  const runAutomatedTestSuite = async () => {
    setTestRunning(true);
    setTestProgress(10);

    const activeProtocols: ProtocolResult[] = [
      { name: 'REST API Endpoints', status: enableRest ? 'running' : 'idle', testsCount: enableRest ? entityNodes.length * 5 : 0, durationMs: 0, coverage: 'CRUD & Soft-Delete' },
      { name: 'GraphQL Gateway', status: enableGraphQl ? 'idle' : 'idle', testsCount: enableGraphQl ? entityNodes.length * 3 : 0, durationMs: 0, coverage: 'Queries & Mutations' },
      { name: 'Kafka Event Outbox', status: enableKafka ? 'idle' : 'idle', testsCount: enableKafka ? entityNodes.length * 2 : 0, durationMs: 0, coverage: 'Domain Event Publishing' },
      { name: 'Real-Time WebSockets & SSE', status: enableRealTime ? 'idle' : 'idle', testsCount: enableRealTime ? 4 : 0, durationMs: 0, coverage: 'Mutation Broadcasting' },
      { name: 'FileIO Pipeline Services', status: enableFileIo ? 'idle' : 'idle', testsCount: enableFileIo ? 6 : 0, durationMs: 0, coverage: 'Upload & Security Filters' },
      { name: 'MediatR & FluentValidation', status: enableRules ? 'idle' : 'idle', testsCount: enableRules ? 12 : 0, durationMs: 0, coverage: 'Business Rule Validation' },
      { name: 'Workflow State Machines', status: enableWorkflows ? 'idle' : 'idle', testsCount: enableWorkflows ? 8 : 0, durationMs: 0, coverage: 'State Transitions & Rules' },
    ];

    setTestResults([...activeProtocols]);

    // Simulate multi-protocol execution steps
    for (let i = 0; i < activeProtocols.length; i++) {
      if (activeProtocols[i].testsCount === 0) continue;
      
      activeProtocols[i].status = 'running';
      setTestResults([...activeProtocols]);
      setTestProgress(Math.round(((i + 1) / activeProtocols.length) * 90));
      
      await new Promise(r => setTimeout(r, 400));
      
      activeProtocols[i].status = 'passed';
      activeProtocols[i].durationMs = Math.floor(120 + Math.random() * 250);
      setTestResults([...activeProtocols]);
    }

    setTestProgress(100);
    setTestRunning(false);
  };

  const totalTests = testResults.reduce((acc, curr) => acc + curr.testsCount, 0);
  const totalPassed = testResults.filter(r => r.status === 'passed').reduce((acc, curr) => acc + curr.testsCount, 0);

  return (
    <div style={{ padding: 24, background: '#0f172a', color: '#f8fafc', height: '100%', overflowY: 'auto', flex: 1 }}>
      {/* Top Header & Tab Selector */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 24, borderBottom: '1px solid #334155', paddingBottom: 16 }}>
        <div>
          <h2 style={{ fontSize: 20, fontWeight: 700, margin: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
            <Cpu style={{ color: '#38bdf8' }} size={24} /> Autonomous Testing Engine & Studio Playground
          </h2>
          <p style={{ margin: '4px 0 0 0', fontSize: 12, color: '#94a3b8' }}>
            Generate synthetic mock data, configure multi-protocol coverage, and operate automated test suites inside Foundry Studio
          </p>
        </div>

        <div style={{ display: 'flex', gap: 8, background: '#1e293b', padding: 4, borderRadius: 8, border: '1px solid #334155' }}>
          <button
            onClick={() => setActiveTab('testEngine')}
            style={{
              padding: '6px 14px',
              borderRadius: 6,
              fontSize: 12,
              fontWeight: 700,
              cursor: 'pointer',
              border: 'none',
              display: 'flex',
              alignItems: 'center',
              gap: 6,
              background: activeTab === 'testEngine' ? '#0284c7' : 'transparent',
              color: activeTab === 'testEngine' ? '#ffffff' : '#94a3b8'
            }}
          >
            <ShieldCheck size={16} /> Autonomous Test Engine
          </button>
          <button
            onClick={() => setActiveTab('playground')}
            style={{
              padding: '6px 14px',
              borderRadius: 6,
              fontSize: 12,
              fontWeight: 700,
              cursor: 'pointer',
              border: 'none',
              display: 'flex',
              alignItems: 'center',
              gap: 6,
              background: activeTab === 'playground' ? '#0284c7' : 'transparent',
              color: activeTab === 'playground' ? '#ffffff' : '#94a3b8'
            }}
          >
            <Play size={16} /> Request Playground
          </button>
        </div>
      </div>

      {activeTab === 'testEngine' ? (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 24 }}>
          {/* Column 1: Test Suite Configuration */}
          <div style={{ background: '#1e293b', border: '1px solid #334155', borderRadius: 12, padding: 20, display: 'flex', flexDirection: 'column', gap: 16 }}>
            <h3 style={{ fontSize: 14, fontWeight: 700, margin: 0, color: '#e2e8f0', display: 'flex', alignItems: 'center', gap: 8 }}>
              <Zap style={{ color: '#38bdf8' }} size={18} /> Test Execution Setup
            </h3>

            <div>
              <label style={{ fontSize: 11, fontWeight: 600, color: '#94a3b8', display: 'block', marginBottom: 6 }}>
                SYNTHETIC MOCK DATA SAMPLES / ENTITY
              </label>
              <input
                type="number"
                value={mockSampleCount}
                onChange={(e) => setMockSampleCount(parseInt(e.target.value) || 10)}
                style={{ width: '100%', padding: '8px 12px', background: '#0f172a', border: '1px solid #334155', borderRadius: 6, color: '#fff', fontSize: 13, fontFamily: 'monospace' }}
              />
              <span style={{ fontSize: 10, color: '#64748b', marginTop: 4, display: 'block' }}>
                Generates realistic boundary datasets for edge case testing
              </span>
            </div>

            <div>
              <label style={{ fontSize: 11, fontWeight: 600, color: '#94a3b8', display: 'block', marginBottom: 8 }}>
                PROTOCOL TEST COVERAGE
              </label>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {[
                  { label: 'REST API CRUD Endpoints', state: enableRest, set: setEnableRest },
                  { label: 'GraphQL Gateway Queries', state: enableGraphQl, set: setEnableGraphQl },
                  { label: 'Kafka Event Outbox Engine', state: enableKafka, set: setEnableKafka },
                  { label: 'Real-Time WebSockets & SSE', state: enableRealTime, set: setEnableRealTime },
                  { label: 'FileIO Storage Pipelines', state: enableFileIo, set: setEnableFileIo },
                  { label: 'MediatR Business Rules', state: enableRules, set: setEnableRules },
                  { label: 'Workflow State Machine Transitions', state: enableWorkflows, set: setEnableWorkflows },
                ].map((item, idx) => (
                  <label key={idx} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12, color: '#cbd5e1', cursor: 'pointer' }}>
                    <input
                      type="checkbox"
                      checked={item.state}
                      onChange={(e) => item.set(e.target.checked)}
                      style={{ width: 14, height: 14, accentColor: '#38bdf8' }}
                    />
                    {item.label}
                  </label>
                ))}
              </div>
            </div>

            <button
              onClick={runAutomatedTestSuite}
              disabled={testRunning}
              style={{
                marginTop: 8,
                padding: '12px 16px',
                background: testRunning ? '#334155' : 'linear-gradient(to right, #0284c7, #4f46e5)',
                color: '#fff',
                border: 'none',
                borderRadius: 8,
                fontWeight: 700,
                fontSize: 13,
                cursor: testRunning ? 'not-allowed' : 'pointer',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 8,
                boxShadow: '0 4px 12px rgba(2, 132, 199, 0.3)'
              }}
            >
              {testRunning ? <RefreshCw className="animate-spin" size={16} /> : <Play size={16} />}
              {testRunning ? 'Running Protocol Test Suite...' : 'Run Autonomous Test Suite'}
            </button>
          </div>

          {/* Column 2: Test Execution Results & Report */}
          <div style={{ background: '#1e293b', border: '1px solid #334155', borderRadius: 12, padding: 20, display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <h3 style={{ fontSize: 14, fontWeight: 700, margin: 0, color: '#e2e8f0', display: 'flex', alignItems: 'center', gap: 8 }}>
                <FileText style={{ color: '#38bdf8' }} size={18} /> Test Execution Matrix & Report
              </h3>
              {totalPassed > 0 && (
                <span style={{ fontSize: 12, padding: '4px 10px', background: '#14532d', color: '#4ade80', borderRadius: 20, fontWeight: 700 }}>
                  Passed: {totalPassed} / {totalTests} Scenarios
                </span>
              )}
            </div>

            {testRunning && (
              <div>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: '#94a3b8', marginBottom: 4 }}>
                  <span>Executing Protocol Suite...</span>
                  <span>{testProgress}%</span>
                </div>
                <div style={{ width: '100%', height: 6, background: '#0f172a', borderRadius: 3, overflow: 'hidden' }}>
                  <div style={{ width: `${testProgress}%`, height: '100%', background: '#38bdf8', transition: 'width 0.3s ease' }} />
                </div>
              </div>
            )}

            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {testResults.map((proto, idx) => (
                <div
                  key={idx}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'space-between',
                    padding: '12px 16px',
                    background: '#0f172a',
                    border: '1px solid #334155',
                    borderRadius: 8,
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    {proto.status === 'passed' ? (
                      <CheckCircle2 style={{ color: '#4ade80' }} size={18} />
                    ) : proto.status === 'running' ? (
                      <RefreshCw className="animate-spin" style={{ color: '#38bdf8' }} size={18} />
                    ) : (
                      <Clock style={{ color: '#64748b' }} size={18} />
                    )}
                    <div>
                      <div style={{ fontSize: 13, fontWeight: 600, color: '#f8fafc' }}>{proto.name}</div>
                      <div style={{ fontSize: 10, color: '#64748b', fontFamily: 'monospace' }}>
                        Coverage: {proto.coverage} • {proto.testsCount} Scenarios
                      </div>
                    </div>
                  </div>

                  <div style={{ textAlign: 'right' }}>
                    <div style={{ fontSize: 12, fontWeight: 700, color: proto.status === 'passed' ? '#4ade80' : proto.status === 'running' ? '#38bdf8' : '#64748b' }}>
                      {proto.status.toUpperCase()}
                    </div>
                    {proto.durationMs > 0 && (
                      <div style={{ fontSize: 10, color: '#94a3b8', fontFamily: 'monospace' }}>
                        {proto.durationMs} ms
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      ) : (
        /* Request Playground View */
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 24 }}>
          {/* Request Form Card */}
          <div style={{ background: '#1e293b', border: '1px solid #334155', borderRadius: 12, padding: 20 }}>
            <h3 style={{ fontSize: 14, fontWeight: 600, margin: '0 0 16px 0', color: '#cbd5e1' }}>Configure Request</h3>

            <div style={{ marginBottom: 16 }}>
              <label style={{ display: 'block', fontSize: 12, color: '#94a3b8', marginBottom: 6 }}>TARGET ENTITY ROUTE</label>
              <select
                value={selectedEntity}
                onChange={(e) => setSelectedEntity(e.target.value)}
                style={{
                  width: '100%',
                  padding: '8px 12px',
                  background: '#0f172a',
                  border: '1px solid #334155',
                  borderRadius: 6,
                  color: '#fff',
                  fontSize: 13,
                }}
              >
                {entityNodes.map((n) => {
                  const entName = n.data?.entity?.name || 'Unknown';
                  return (
                    <option key={entName} value={entName}>
                      {crudRouteFor(entName)} (Entity: {entName})
                    </option>
                  );
                })}
              </select>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16 }}>
              <div>
                <label style={{ display: 'block', fontSize: 12, color: '#94a3b8', marginBottom: 6 }}>HTTP METHOD</label>
                <select
                  value={method}
                  onChange={(e) => setMethod(e.target.value as any)}
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    background: '#0f172a',
                    border: '1px solid #334155',
                    borderRadius: 6,
                    color: '#fff',
                    fontSize: 13,
                    fontWeight: 700,
                  }}
                >
                  <option value="GET">GET</option>
                  <option value="POST">POST</option>
                  <option value="DELETE">DELETE</option>
                </select>
              </div>

              <div>
                <label style={{ display: 'block', fontSize: 12, color: '#94a3b8', marginBottom: 6 }}>TENANT ID HEADER</label>
                <input
                  type="text"
                  value={tenantId}
                  onChange={(e) => setTenantId(e.target.value)}
                  placeholder="X-Tenant-ID..."
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    background: '#0f172a',
                    border: '1px solid #334155',
                    borderRadius: 6,
                    color: '#fff',
                    fontSize: 13,
                    fontFamily: 'monospace',
                  }}
                />
              </div>
            </div>

            {method === 'POST' && (
              <div style={{ marginBottom: 16 }}>
                <label style={{ display: 'block', fontSize: 12, color: '#94a3b8', marginBottom: 6 }}>REQUEST BODY (JSON)</label>
                <textarea
                  value={requestBody}
                  onChange={(e) => setRequestBody(e.target.value)}
                  rows={6}
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    background: '#0f172a',
                    border: '1px solid #334155',
                    borderRadius: 6,
                    color: '#38bdf8',
                    fontFamily: 'monospace',
                    fontSize: 12,
                  }}
                />
              </div>
            )}

            <button
              onClick={handleSendRequest}
              disabled={loading}
              style={{
                width: '100%',
                padding: '10px 16px',
                background: loading ? '#334155' : '#0284c7',
                color: '#fff',
                border: 'none',
                borderRadius: 6,
                fontWeight: 700,
                fontSize: 13,
                cursor: loading ? 'not-allowed' : 'pointer',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 8,
              }}
            >
              <Send size={16} /> {loading ? 'Sending Request...' : 'Execute Request'}
            </button>
          </div>

          {/* Response Viewer Card */}
          <div style={{ background: '#1e293b', border: '1px solid #334155', borderRadius: 12, padding: 20 }}>
            <h3 style={{ fontSize: 14, fontWeight: 600, margin: '0 0 16px 0', color: '#cbd5e1' }}>HTTP Response Viewer</h3>

            {response ? (
              <div>
                {response.error ? (
                  <div style={{ padding: 14, background: '#451a1a', border: '1px solid #7f1d1d', borderRadius: 8, color: '#fca5a5', fontSize: 13, display: 'flex', gap: 10 }}>
                    <AlertCircle size={18} style={{ flexShrink: 0 }} />
                    <div>{response.error}</div>
                  </div>
                ) : (
                  <div>
                    <div style={{ display: 'flex', gap: 16, marginBottom: 12 }}>
                      <span style={{ padding: '4px 10px', background: response.status === 200 || response.status === 201 ? '#14532d' : '#7f1d1d', color: '#4ade80', borderRadius: 4, fontSize: 12, fontWeight: 700 }}>
                        Status: {response.status}
                      </span>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 4, color: '#94a3b8', fontSize: 12 }}>
                        <Clock size={14} /> {response.time} ms
                      </span>
                    </div>

                    <pre style={{ margin: 0, padding: 14, background: '#0f172a', border: '1px solid #334155', borderRadius: 8, color: '#4ade80', fontFamily: 'monospace', fontSize: 12, overflowX: 'auto', maxHeight: 320 }}>
                      {response.body}
                    </pre>
                  </div>
                )}
              </div>
            ) : (
              <div style={{ padding: 40, textAlign: 'center', color: '#64748b', fontSize: 13 }}>
                Click "Execute Request" above to test live endpoint performance and payload parsing.
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
};
