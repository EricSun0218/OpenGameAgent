using System.Threading.Tasks;
using OpenGameAgent.Unity;
using UnityEngine;

public sealed class OpenGameAgentQuickstart : MonoBehaviour
{
    [SerializeField] private OpenGameAgentBehaviour agent;

    private void OnEnable()
    {
        agent.EventReceived += OnEvent;
    }

    private void OnDisable()
    {
        agent.EventReceived -= OnEvent;
    }

    public Task AskAsync(string completeGameInputJson)
    {
        return agent.RunAsync(completeGameInputJson);
    }

    private static void OnEvent(GameAgentStreamEvent item)
    {
        Debug.Log(item.Name + ": " + item.Json);
    }
}
