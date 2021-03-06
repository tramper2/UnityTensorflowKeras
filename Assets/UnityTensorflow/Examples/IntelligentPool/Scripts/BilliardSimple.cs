﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BilliardSimple : MonoBehaviour, IESOptimizable
{
    
    protected BilliardGameSystem gameSystem;
    public ESOptimizer optimizer;
    
    private void Start()
    {
        gameSystem = FindObjectOfType(typeof(BilliardGameSystem)) as BilliardGameSystem;
        Debug.Assert(gameSystem != null, "Did not find BilliardGameSystem in the scene");
    }

    private void Update()
    {
        if (optimizer.IsOptimizing)
        {
            gameSystem.EvaluateShot(ParamsToForceVector(optimizer.BestParams), Color.green);
        }
    }

    public List<float> Evaluate(List<double[]> action)
    {
        List<Vector3> forces = new List<Vector3>();
        for (int i = 0; i < action.Count; ++i)
        {
            forces.Add(ParamsToForceVector(action[i]));
        }
        var values = gameSystem.EvaluateShotBatch(forces, Color.gray);
        return values;
    }

    public void OnReady(double[] vectorAction)
    {
        gameSystem.Shoot(ParamsToForceVector(vectorAction));
        Physics.autoSimulation = true;
    }

    public Vector3 ParamsToForceVector(double[] x)
    {
        Vector3 force = (new Vector3((float)x[0], 0, (float)x[1]));
        //if (force.magnitude > maxForce)
        //force = maxForce * force.normalized;
        return force;
    }
    public Vector3 SamplePointToForceVectorRA(float x, float y)
    {
        x = Mathf.Clamp01(x); y = Mathf.Clamp01(y);
        float angle = x * Mathf.PI * 2;
        float force = y;
        double[] param = new double[2];
        param[0] = Mathf.Sin(angle) * force;
        param[1] = Mathf.Cos(angle) * force;
        return ParamsToForceVector(param);
    }

    public Vector3 SamplePointToForceVectorXY(float x, float y)
    {
        x = Mathf.Clamp01(x); y = Mathf.Clamp01(y);
        float fx = x - 0.5f;
        float fy = y - 0.5f;

        double[] param = new double[2];
        param[0] = fx * 2;
        param[1] = fy * 2;
        return ParamsToForceVector(param);
    }

    public int GetParamDimension()
    {
        return 2;
    }
}
