---
name: aspire-deploy-aws
description: Observe and diagnose .NET Aspire deployments to AWS (via Aspire.Hosting.AWS / CDK). Use when the user asks to check deployment status, read container logs, diagnose a stuck or failing deployment, or find public service endpoints after a deploy.
argument-hint: "[stack-name]"
---

# Skill: Observe .NET Aspire AWS Deployments

## Prerequisites

- `AWS_PROFILE` must be set in the environment, or use `AWS_PROFILE=<profile>` prefix on every command.
- The CloudFormation stack name matches the `name:` argument passed to `AddAWSCDKEnvironment` in AppHost.cs.
- Region must be set to match your deployment target.

Set these as shell variables at the start of each session:
```bash
STACK="$ARGUMENTS"   # pass the stack name as the skill argument: /aspire-deploy-aws <stack-name>
REGION=<your-region>
PROFILE=<your-aws-profile>
```

---

## Step 1 — Check stack status

```bash
AWS_PROFILE=$PROFILE aws cloudformation describe-stacks \
  --stack-name $STACK --region $REGION \
  --query 'Stacks[0].{Status:StackStatus,Updated:LastUpdatedTime}'
```

Key states:
| State | Meaning |
|---|---|
| `CREATE_IN_PROGRESS` | Deploying — check recent events |
| `CREATE_COMPLETE` | Fully deployed |
| `ROLLBACK_IN_PROGRESS` | Something failed, rolling back |
| `ROLLBACK_COMPLETE` | Rolled back — must delete before redeploying |
| `DELETE_IN_PROGRESS` | Being torn down |

---

## Step 2 — Check recent events

```bash
AWS_PROFILE=$PROFILE aws cloudformation describe-stack-events \
  --stack-name $STACK --region $REGION \
  --query 'StackEvents[0:10].{Time:Timestamp,Resource:LogicalResourceId,Status:ResourceStatus,Reason:ResourceStatusReason}' \
  --output table
```

Look for `CREATE_FAILED` entries — the `Reason` column explains what went wrong.

---

## Step 3 — Find the correct log groups

**Important:** Every time the stack is deleted and recreated, CloudFormation generates new log group names with a different random suffix. Always look up the current log groups rather than hardcoding names.

```bash
AWS_PROFILE=$PROFILE aws logs describe-log-groups --region $REGION \
  --query 'logGroups[?contains(logGroupName, `<project-name>`)].{Name:logGroupName,Created:creationTime}'
```

Match the `Created` timestamp to the current stack's `LastUpdatedTime` to identify the active log groups. The ones created AFTER the stack started are the current ones.

There will typically be one log group per service (e.g. `...-<ServiceName>TaskDef...-<suffix>`).

---

## Step 4 — Read container logs

```bash
# Most recent streams (to get the current log stream name)
AWS_PROFILE=$PROFILE aws logs describe-log-streams \
  --log-group-name "<log-group-name>" \
  --region $REGION --order-by LastEventTime --descending \
  --query 'logStreams[0:3].{Stream:logStreamName,LastMs:lastEventTimestamp}'

# Tail recent logs
AWS_PROFILE=$PROFILE aws logs tail "<log-group-name>" \
  --region $REGION --since 10m 2>&1 | tail -40

# Read a specific stream directly
AWS_PROFILE=$PROFILE aws logs get-log-events \
  --log-group-name "<log-group-name>" \
  --log-stream-name "<stream-name>" \
  --region $REGION --limit 50 \
  --query 'events[].message' --output text
```

---

## Step 5 — Check ECS tasks

```bash
# Find the cluster ARN
AWS_PROFILE=$PROFILE aws ecs list-clusters --region $REGION

# List running tasks
AWS_PROFILE=$PROFILE aws ecs list-tasks --cluster <cluster-arn> --region $REGION

# Describe tasks (health, stop reason)
AWS_PROFILE=$PROFILE aws ecs describe-tasks \
  --cluster <cluster-arn> --region $REGION \
  --tasks <task-arn1> <task-arn2> \
  --query 'tasks[].{Name:group,Status:lastStatus,Health:healthStatus,StopReason:stoppedReason}'
```

---

## Step 6 — Find public endpoints

Once deployed, get the ALB DNS names:

```bash
AWS_PROFILE=$PROFILE aws elbv2 describe-load-balancers --region $REGION \
  --query 'LoadBalancers[].{Name:LoadBalancerName,DNS:DNSName,State:State.Code}'
```

To identify which ALB belongs to which service, check tags:

```bash
AWS_PROFILE=$PROFILE aws elbv2 describe-tags --region $REGION \
  --resource-arns $(AWS_PROFILE=$PROFILE aws elbv2 describe-load-balancers \
    --region $REGION --query 'LoadBalancers[].LoadBalancerArn' --output text) \
  --query 'TagDescriptions[].{ARN:ResourceArn,Tags:Tags}'
```

Look for `aws:cloudformation:logical-id` tag to match ALB → service.

After deployment completes, stack outputs also contain the endpoints:

```bash
AWS_PROFILE=$PROFILE aws cloudformation describe-stacks \
  --stack-name $STACK --region $REGION \
  --query 'Stacks[0].Outputs'
```

---

## Common failures and fixes

### `ResourceInitializationError: ECR i/o timeout`
Tasks in public subnets without public IPs can't reach ECR. Cause: `PublishAsECSFargateExpressService` puts tasks in public subnets without assigning public IPs, and no VPC endpoints are configured. Fix: switch to `PublishAsECSFargateServiceWithALB`.

### `UriFormatException: Invalid URI` when resolving a service endpoint
`configuration["services:<ServiceName>:https:0"]` returns null because the ALB publish target injects the key with `http` not `https`. Fix: fall back to `http:0`:
```csharp
var serviceUrl = configuration["services:<ServiceName>:https:0"]
    ?? configuration["services:<ServiceName>:http:0"]
    ?? throw new InvalidOperationException("<ServiceName> endpoint is not configured.");
```

### Stack stuck in `ROLLBACK_COMPLETE`
Cannot redeploy to a stack in this state. Must delete first:
```bash
AWS_PROFILE=$PROFILE aws cloudformation delete-stack --stack-name $STACK --region $REGION
```
Wait for `DELETE_COMPLETE`, then redeploy.

### Health check failing (19-minute silent timeout)
ECS waits for ALB health checks to pass. If `/ping` returns non-200 or the app crashes before starting, ECS keeps retrying until CloudFormation times out. Check container logs immediately — the crash reason will be there.

### `SocketException (13): Permission denied` on port bind
The CDK template passes `HTTP_PORTS={unresolved placeholder}` at runtime, causing ASP.NET Core to fall back to port 80 (requires root). Fix: bake `ASPNETCORE_URLS=http://+:8080` into the container image via `ContainerEnvironmentVariable` in the `.csproj`.
