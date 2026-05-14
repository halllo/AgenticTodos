using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public class ThreejsApp()
{
    const string URI = "ui://threejs/mcp-app.html";

    const string DefaultCode = """
        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000);
        const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
        renderer.setSize(width, height);
        renderer.setClearColor(0x1a1a2e);

        const controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;

        const cube = new THREE.Mesh(
          new THREE.BoxGeometry(1, 1, 1),
          new THREE.MeshStandardMaterial({ color: 0x00ff88 })
        );
        cube.rotation.x = 0.5;
        cube.rotation.y = 0.7;
        scene.add(cube);

        const keyLight = new THREE.DirectionalLight(0xffffff, 1.2);
        keyLight.position.set(1, 1, 2);
        scene.add(keyLight);
        scene.add(new THREE.AmbientLight(0x404040, 0.5));

        camera.position.z = 3;

        function animate() {
          requestAnimationFrame(animate);
          controls.update();
          renderer.render(scene, camera);
        }
        animate();
        """;

    const string Documentation = """
        # Three.js Widget Documentation

        ## Available Globals
        - `THREE` - Three.js library (r181)
        - `canvas` - Pre-created canvas element
        - `width`, `height` - Canvas dimensions in pixels
        - `OrbitControls` - Interactive camera controls
        - `EffectComposer`, `RenderPass`, `UnrealBloomPass` - Post-processing effects

        ## Basic Template
        ```javascript
        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000);
        const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
        renderer.setSize(width, height);
        renderer.setClearColor(0x1a1a2e);  // Dark background

        // Add objects here...

        camera.position.z = 5;
        renderer.render(scene, camera);  // Static render
        ```

        ## Example: Rotating Cube with Lighting
        ```javascript
        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000);
        const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
        renderer.setSize(width, height);
        renderer.setClearColor(0x1a1a2e);

        const cube = new THREE.Mesh(
          new THREE.BoxGeometry(1, 1, 1),
          new THREE.MeshStandardMaterial({ color: 0x00ff88 })
        );
        scene.add(cube);

        // Lighting - keep intensity at 1 or below
        scene.add(new THREE.DirectionalLight(0xffffff, 1));
        scene.add(new THREE.AmbientLight(0x404040));

        camera.position.z = 3;

        function animate() {
          requestAnimationFrame(animate);
          cube.rotation.x += 0.01;
          cube.rotation.y += 0.01;
          renderer.render(scene, camera);
        }
        animate();
        ```

        ## Example: Interactive OrbitControls
        ```javascript
        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000);
        const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
        renderer.setSize(width, height);
        renderer.setClearColor(0x2d2d44);

        const controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;

        const sphere = new THREE.Mesh(
          new THREE.SphereGeometry(1, 32, 32),
          new THREE.MeshStandardMaterial({ color: 0xff6b6b, roughness: 0.4 })
        );
        scene.add(sphere);

        scene.add(new THREE.DirectionalLight(0xffffff, 1));
        scene.add(new THREE.AmbientLight(0x404040));

        camera.position.z = 4;

        function animate() {
          requestAnimationFrame(animate);
          controls.update();
          renderer.render(scene, camera);
        }
        animate();
        ```

        ## Tips
        - Always set `renderer.setClearColor()` to a dark color
        - Keep light intensity ≤ 1 to avoid washed-out scenes
        - Use `MeshStandardMaterial` for realistic lighting
        - For animations, use `requestAnimationFrame`
        """;

    [McpServerTool, Description("Render an interactive 3D scene with custom Three.js code. Available globals: THREE, OrbitControls, EffectComposer, RenderPass, UnrealBloomPass, canvas, width, height.")]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]
    public IEnumerable<ContentBlock> ShowThreejsScene(
        [Description("JavaScript code to render the 3D scene")] string? code = null,
        [Description("Height in pixels")] int height = 400)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(new { code = code ?? DefaultCode, height });
        return [new TextContentBlock { Text = data }];
    }

    [McpServerTool, Description("Get documentation and examples for using the Three.js widget")]
    public IEnumerable<ContentBlock> LearnThreejs() =>
    [
        new TextContentBlock { Text = Documentation },
    ];

    [McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
    public async Task<string> ThreejsUIResource() =>
        await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "threejs-app", "dist", "mcp-app.html"));
}
