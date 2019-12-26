using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class ChunkGenerator : MonoBehaviour {

    [Header("General Settings")]
    public DensityGenerator densityGenerator;
    public Material material;
    public ComputeShader chunkShader;
    public bool editorAutoUpdate = true;
    public bool playingAutoUpdate = true;
    public bool generateColliders;

    [Header("Fixed Map Size Settings")]
    public bool mapSizeFixed;
    public Vector3Int numberOfChunks = Vector3Int.one;

    [Header("Chunk Settings")]
    public float surfaceLevel;
    [Range(1, 64)]
    public float chunkSize = 1f;
    public Vector3 densityOffset;
    [Range(1, 64)]
    public int resolution = 32;

    [Header("Gizmos")]
    public bool drawChunkGizmos;
    public Color chunkGizmosColor = Color.white;

    // Chunk Data Structures
    string chunkContainerName = "Chunk Container";
    GameObject chunkContainer;
    List<Chunk> chunks;

    // Compute Buffers
    ComputeBuffer vertexBuffer;
    ComputeBuffer triangleBuffer;
    ComputeBuffer numTriangleBuffer;

    // Flags
    bool settingsUpdated;

    void OnValidate() {
        settingsUpdated = true;
    }

    void Awake() {
        if (Application.isPlaying && !mapSizeFixed) {
            InitChunkDS();

            var existingChunks = FindObjectsOfType<Chunk>();
            foreach (Chunk chunk in existingChunks) {
                chunk.DestroyChunk();
            }
        }
    }

    void Update() {
        if (Application.isPlaying && !mapSizeFixed) {
            Run();
        }

        if (settingsUpdated || densityGenerator.settingsUpdated) {
            RequestChunkUpdate();
            settingsUpdated = false;
            densityGenerator.settingsUpdated = false;
        }
    }

    void OnDestroy() {
        ReleaseBuffers();
    }

    public void Run() {
        InitBuffers();

        if (mapSizeFixed) {
            InitChunks();
            UpdateAllChunks();
        } else {
            if (Application.isPlaying) {
                // Initialise only those visible ones
            }
        }
        if (!Application.isPlaying) {
            ReleaseBuffers();
        }
    }

    public void RequestChunkUpdate() {
        if ((Application.isPlaying && playingAutoUpdate) || (!Application.isPlaying && editorAutoUpdate)) {
            Run();
        }
    }

    public void UpdateAllChunks() {
        foreach (Chunk chunk in chunks) {
            UpdateChunk(chunk);
        }
    }

    public void UpdateChunk(Chunk chunk) {
        int numVertsPerAxis = resolution + 1;
        Vector3 mapSize = (Vector3)numberOfChunks * chunkSize;
        float vertSpacing = chunkSize / resolution;
        Vector3Int position = chunk.position;
        Vector3 center = GetChunkCenterFromPosition(position);

        // Obtain density of each vertex
        densityGenerator.Generate(vertexBuffer, numVertsPerAxis, chunkSize, vertSpacing, mapSize, center, densityOffset);

        // Set up compute shader
        int kernelIndex = chunkShader.FindKernel("CubeMarch");
        triangleBuffer.SetCounterValue(0);
        chunkShader.SetBuffer(kernelIndex, "vertexBuffer", vertexBuffer);
        chunkShader.SetBuffer(kernelIndex, "triangleBuffer", triangleBuffer);
        chunkShader.SetInt("resolution", resolution);
        chunkShader.SetFloat("surfaceLevel", surfaceLevel);
        chunkShader.Dispatch(kernelIndex, 8, 8, 8);

        // Obtain compute shader result
        int[] numTriangleOut = { 0 };
        ComputeBuffer.CopyCount(triangleBuffer, numTriangleBuffer, 0);
        numTriangleBuffer.GetData(numTriangleOut);
        int numTriangles = numTriangleOut[0];

        Triangle[] triangleList = new Triangle[numTriangles];
        triangleBuffer.GetData(triangleList, 0, 0, numTriangles);

        // Generate mesh
        Mesh mesh = chunk.mesh;
        Vector3[] vertices = new Vector3[numTriangles * 3];
        int[] triangles = new int[numTriangles * 3];

        for (int triIndex = 0; triIndex < numTriangles; triIndex++) {
            for (int vertIndex = 0; vertIndex < 3; vertIndex++) {
                vertices[triIndex * 3 + vertIndex] = triangleList[triIndex][vertIndex];
                triangles[triIndex * 3 + vertIndex] = triIndex * 3 + vertIndex;
            }
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
    }

    void GetChunkContainer() {
        chunkContainer = GameObject.Find(chunkContainerName);
        if (chunkContainer == null) {
            chunkContainer = new GameObject(chunkContainerName);
        }
    }

    Vector3 GetChunkCenterFromPosition(Vector3Int position) {
        if (!mapSizeFixed) {
            return (Vector3)position * chunkSize;
        }

        // If on restricted map size, center is "origin"
        Vector3 mapSize = (Vector3)numberOfChunks * chunkSize;
        return (Vector3)position * chunkSize + Vector3.one * chunkSize / 2f - mapSize / 2f;
    }

    void InitChunkDS() {
        chunks = new List<Chunk>();
    }

    void InitBuffers() {
        // Values per chunk
        int numVertsPerAxis = resolution + 1;
        int numVerts = numVertsPerAxis * numVertsPerAxis * numVertsPerAxis;
        int numVoxels = resolution * resolution * resolution;
        int maxTriangleCount = numVoxels * 5;

        if (!Application.isPlaying || vertexBuffer == null || numVerts != vertexBuffer.count) {
            if (Application.isPlaying) {
                ReleaseBuffers();
            }

            // vertex buffer has size of 4 floats to account for density value;
            vertexBuffer = new ComputeBuffer(numVerts, sizeof(float) * 4);
            triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 3 * 3, ComputeBufferType.Append);
            numTriangleBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        }
    }

    // (Re)Initialize chunks
    // Adds new chunks as necessary, and removes those unnecessary.
    void InitChunks() {
        GetChunkContainer();
        chunks = new List<Chunk>();
        Dictionary<Vector3Int, Chunk> oldChunks = new Dictionary<Vector3Int, Chunk>();
        foreach (Chunk chunk in FindObjectsOfType<Chunk>()) {
            oldChunks.Add(chunk.position, chunk);
        }
        for (int x = 0; x < numberOfChunks.x; x++) {
            for (int y = 0; y < numberOfChunks.y; y++) {
                for (int z = 0; z < numberOfChunks.z; z++) {
                    Vector3Int position = new Vector3Int(x, y, z);
                    Chunk chunkToAdd;
                    if (oldChunks.ContainsKey(position)) {
                        chunkToAdd = oldChunks[position];
                        oldChunks.Remove(position);
                    } else {
                        chunkToAdd = CreateChunk(position);
                    }

                    chunkToAdd.InitializeMesh(material, generateColliders);
                    chunks.Add(chunkToAdd);
                }
            }
        }

        foreach (Chunk chunk in oldChunks.Values) {
            chunk.DestroyChunk();
        }
    }

    Chunk CreateChunk(Vector3Int position) {
        GameObject chunkObj = new GameObject($"Chunk@({position.x}, {position.y}, {position.z})");
        chunkObj.transform.parent = chunkContainer.transform;
        Chunk chunk = chunkObj.AddComponent<Chunk>();
        chunk.position = position;

        return chunk;
    }

    void ReleaseBuffers() {
        if (vertexBuffer != null) {
            vertexBuffer.Release();
        }
        if (triangleBuffer != null) {
            triangleBuffer.Release();
        }
        if (numTriangleBuffer != null) {
            numTriangleBuffer.Release();
        }
    }

    void OnDrawGizmos() {
        if (drawChunkGizmos) {
            foreach (Chunk chunk in chunks) {
                Vector3 chunkCenter = GetChunkCenterFromPosition(chunk.position);
                Gizmos.color = chunkGizmosColor;
                Gizmos.DrawWireCube(chunkCenter, Vector3.one * chunkSize);
            }
        }
    }
}