import { useEffect, useState } from 'react'
import './App.css'

const API_URL = import.meta.env.VITE_API_URL ?? '/api'

function App() {
  const [apiStatus, setApiStatus] = useState<string>('checking...')

  useEffect(() => {
    fetch(`${API_URL}/health`)
      .then((res) => (res.ok ? res.json() : Promise.reject(res.status)))
      .then((data) => setApiStatus(`ok (${data.service})`))
      .catch(() => setApiStatus('unreachable'))
  }, [])

  return (
    <div className="landing">
      <h1>Z.Vtuber Open Platform</h1>
      <p>Welcome to the Z.Vtuber open platform web console.</p>
      <p>
        API status: <strong>{apiStatus}</strong>
      </p>
    </div>
  )
}

export default App
