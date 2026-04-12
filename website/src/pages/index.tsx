import type {ReactNode} from 'react';
import {useEffect} from 'react';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';

function Hero() {
  const {siteConfig} = useDocusaurusContext();
  return (
    <header style={{padding: '4rem 0', textAlign: 'center', background: 'var(--ifm-color-primary-lightest)'}}>
      <div className="container">
        <Heading as="h1" style={{fontSize: '3rem'}}>
          {siteConfig.title}
        </Heading>
        <p style={{fontSize: '1.4rem', opacity: 0.85}}>
          {siteConfig.tagline}
        </p>
        <div style={{display: 'flex', gap: '1rem', justifyContent: 'center', marginTop: '2rem'}}>
          <Link className="button button--primary button--lg" to="/docs">
            Get Started
          </Link>
          <Link className="button button--secondary button--lg" href="https://github.com/Neftedollar/FsMcp">
            GitHub
          </Link>
        </div>
      </div>
    </header>
  );
}

const features = [
  {
    title: 'Type-Safe by Default',
    description: 'Smart constructors, discriminated unions, and Result types. No obj in the public API. Invalid states are unrepresentable.',
  },
  {
    title: 'Computation Expressions',
    description: 'mcpServer { } CE for declarative server definition. TypedTool.define<\'T> auto-generates JSON Schema from F# records via TypeShape.',
  },
  {
    title: 'Batteries Included',
    description: 'Middleware pipeline, telemetry, validation, streaming, notifications, hot reload, and testing utilities — all composable.',
  },
];

function Features() {
  return (
    <section style={{padding: '4rem 0'}}>
      <div className="container">
        <div className="row">
          {features.map((f, idx) => (
            <div key={idx} className="col col--4" style={{marginBottom: '2rem'}}>
              <Heading as="h3">{f.title}</Heading>
              <p>{f.description}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function CodeExample() {
  return (
    <section style={{padding: '2rem 0 4rem', background: 'var(--ifm-background-surface-color)'}}>
      <div className="container">
        <Heading as="h2" style={{textAlign: 'center', marginBottom: '2rem'}}>
          Define an MCP server in 10 lines
        </Heading>
        <pre style={{padding: '1.5rem', borderRadius: '8px', fontSize: '0.9rem', maxWidth: '700px', margin: '0 auto'}}>
{`type GreetArgs = { name: string; greeting: string option }

let server = mcpServer {
    name "MyServer"
    version "1.0.0"
    tool (TypedTool.define<GreetArgs> "greet" "Greets"
        (fun args -> task {
            let g = args.greeting |> Option.defaultValue "Hello"
            return Ok [ Content.text $"{g}, {args.name}!" ]
        }) |> unwrapResult)
    useStdio
}

Server.run server`}
        </pre>
        <p style={{textAlign: 'center', marginTop: '1rem', opacity: 0.7}}>
          JSON Schema auto-generated from GreetArgs. name=required, greeting=optional.
        </p>
      </div>
    </section>
  );
}

function Install() {
  return (
    <section style={{padding: '3rem 0', textAlign: 'center'}}>
      <div className="container">
        <Heading as="h2">Install</Heading>
        <pre style={{display: 'inline-block', padding: '1rem 2rem', borderRadius: '8px', fontSize: '1rem', marginTop: '1rem'}}>
          dotnet add package FsMcp.Server
        </pre>
        <p style={{marginTop: '1rem'}}>
          <Link to="/docs/getting-started">Full install guide →</Link>
        </p>
      </div>
    </section>
  );
}

export default function Home(): ReactNode {
  useEffect(() => {
    const nav = navigator as Navigator & { mcpActions?: { register?: (action: unknown) => void } };
    if (!nav.mcpActions || typeof nav.mcpActions.register !== 'function') return;

    const register = (action: unknown) => {
      try {
        nav.mcpActions?.register?.(action);
      } catch {
        // Ignore draft WebMCP runtime incompatibilities.
      }
    };

    register({
      id: 'fsmcp-open-docs',
      name: 'Open FsMcp Docs',
      description: 'Open FsMcp documentation homepage.',
      parameters: { type: 'object', properties: {} },
      handler: async () => {
        window.location.assign('https://neftedollar.com/FsMcp/docs');
        return { success: true, url: 'https://neftedollar.com/FsMcp/docs' };
      },
    });

    register({
      id: 'fsmcp-open-server-guide',
      name: 'Open Server Guide',
      description: 'Open FsMcp server guide.',
      parameters: { type: 'object', properties: {} },
      handler: async () => {
        window.location.assign('https://neftedollar.com/FsMcp/docs/server-guide');
        return { success: true, url: 'https://neftedollar.com/FsMcp/docs/server-guide' };
      },
    });

    register({
      id: 'fsmcp-open-repository',
      name: 'Read FsMcp Source Code',
      description: 'Open FsMcp GitHub repository to inspect implementation code.',
      parameters: { type: 'object', properties: {} },
      handler: async () => {
        window.location.assign('https://github.com/Neftedollar/FsMcp');
        return { success: true, url: 'https://github.com/Neftedollar/FsMcp' };
      },
    });
  }, []);

  return (
    <Layout title="Home" description="Build MCP servers in F# with type safety and zero boilerplate">
      <Hero />
      <main>
        <Features />
        <CodeExample />
        <Install />
      </main>
    </Layout>
  );
}
