﻿using System.Collections.Generic;
using UnityEngine;


#if UNITY_EDITOR
using UnityEditor;

using NWH;

// Replay
public partial class Grapher : EditorWindow
{
    private static List<string> replayFiles = new List<string>();

    private void ReplayInit()
    {
        // No replay files, ask user to add some
        if (replayFiles.Count == 0 && channels.Count == 0)
        {
            Debug.LogWarning("No replay files selected.");
        }

        // If files have been added, get samples
        if (replayFiles.Count > 0)
        {
            for (int i = 0; i < replayFiles.Count; i++)
            {
                List<Sample> gs = FileHandler.LoadSamplesFromCSV(replayFiles[i]);
                string header = FileHandler.LoadHeaderFromCSV(replayFiles[i]);

                // If replay file valid
                if (header != null)
                {
                    string[] hs = header.Split(',');
                    if (hs.Length == 5)
                    {
                        Channel ch = null;
                        string name = hs[0] + " [Re]";
                        int nameI = 0;
                        while ((ch = channels.Find(x => x.name == name)) != null)
                        {
                            nameI++;
                            name = hs[0] + "(" + nameI + ")" + " [Re]";
                        }
                        ch = AddChannel();
                        ch.name = name;
                        ch.color = new Color(float.Parse(hs[2]), float.Parse(hs[3]), float.Parse(hs[4]), 1f);
                        // Self get
                        ch.verticalResolution = ch.verticalResolution;
                        ch.LogToFile = false;

                        foreach (Sample g in gs)
                        {
                            ch.Enqueue(g.y, g.time, g.x);
                        }

                    }
                    else
                    {
                        Debug.LogWarning("Invalid header size. Skipping.");
                    }
                }
                else
                {
                    Debug.LogWarning("Replay file is missing header. Skipping.");
                }
            }
        }
    }

    private void OpenFiles()
    {
        List<string> files = FileHandler.BrowserOpenFiles();

        // Check if user has completed the action
        replayFiles = new List<string>();
        if (files != null)
        {
            replayFiles.AddRange(files);
        }
    }
}

#endif