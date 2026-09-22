using Unity.InferenceEngine;

public static class BodyOracleModel
{
    /// <summary>Specialize the dynamic ONNX graph once, before frame processing.
    /// Static shape expressions are folded into constants instead of executed on the CPU every frame.</summary>
    public static Model Load(ModelAsset asset)
    {
        var source = ModelLoader.Load(asset);
        var graph = new FunctionalGraph();
        var input = graph.AddInput<float>(new TensorShape(1, 3, 640, 640), source.inputs[0].name);
        graph.AddOutputs(Functional.Forward(source, input));
        return graph.Compile();
    }
}
