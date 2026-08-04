using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LogWindow : MonoBehaviour
{
    public TextMeshProUGUI text;

    public ScrollRect scrollRect;

    private static LogWindow _instance;

    public RectTransform rectTransform;

    /// <summary>
    /// Messages logged from non-main threads, drained in Update.
    /// </summary>
    /// <remarks>
    /// The socket callbacks (BeginConnect/BeginReceive) run on thread pool threads and log from
    /// there. Touching Unity APIs off the main thread throws, and in ConnectCallback that throw is
    /// swallowed by the surrounding catch, which then sets state to CONNECT_ERROR -- reporting a
    /// failure for a connection that had in fact been established.
    /// </remarks>
    private static readonly Queue<string> PendingMessages = new Queue<string>();

    private const int MaxPendingMessages = 256;

    private void Awake()
    {
        _instance = this;
    }

    private void Update()
    {
        // Kept short: the lock is contended by socket threads on every log line.
        while (true)
        {
            string message;
            lock (PendingMessages)
            {
                if (PendingMessages.Count == 0)
                {
                    return;
                }

                message = PendingMessages.Dequeue();
            }

            AppendText(message);
        }
    }

    private IEnumerator AutoScrollCoroutine()
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.content as RectTransform);
        yield return new WaitForEndOfFrame(); // Wait one frame for layout to update

        // Update rectTransform height based on text content
        UpdateRectTransformHeight();

        scrollRect.verticalNormalizedPosition = 0f;
    }

    private void UpdateRectTransformHeight()
    {
        if (rectTransform != null && text != null)
        {
            // Force the text to update its preferred height
            text.ForceMeshUpdate();

            // Get the preferred height of the text
            float preferredHeight = text.preferredHeight;

            // Update the rectTransform height
            Vector2 sizeDelta = rectTransform.sizeDelta;
            sizeDelta.y = preferredHeight + 10f; // Add some padding
            rectTransform.sizeDelta = sizeDelta;
        }
    }

    public void AppendText(string message)
    {
        // add time prefix of local timezone to the message
        string timePrefix = $"[{System.DateTime.Now:HH:mm:ss}] ";
        text.text += $"{timePrefix}{message}\n";

        StartCoroutine(AutoScrollCoroutine());
    }

    private static void Message(string message)
    {
        // Callers include socket threads, so nothing here may touch Unity APIs directly: even
        // reading _instance's implicit bool operator (the != null null-check on a MonoBehaviour)
        // is a main-thread call. Queue unconditionally and let Update resolve the instance.
        lock (PendingMessages)
        {
            // Bounded because Update only drains while a LogWindow instance is alive; without a
            // cap, logging from socket threads before/after that would grow without limit.
            if (PendingMessages.Count >= MaxPendingMessages)
            {
                PendingMessages.Dequeue();
            }

            PendingMessages.Enqueue(message);
        }
    }

    public static void Info(string info)
    {
        // white color text
        Message($"<color=white>{info}</color>");
    }

    public static void Warn(string info)
    {
        // yellow color text
        Message($"<color=yellow>{info}</color>");
    }

    public static void Error(string info)
    {
        // red color text
        Message($"<color=red>{info}</color>");
    }
}