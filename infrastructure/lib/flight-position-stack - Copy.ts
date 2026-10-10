import {
  Duration,
  RemovalPolicy,
  Stack,
  StackProps,
  CfnOutput,
} from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as sources from 'aws-cdk-lib/aws-lambda-event-sources';
import * as apigw from 'aws-cdk-lib/aws-apigateway';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as path from 'path';

interface Props extends StackProps {
  persistent: boolean;
}

export class FlightPositionStack extends Stack {
  constructor(scope: Construct, id: string, props: Props) {
    super(scope, id, props);

    const removal = props.persistent
      ? RemovalPolicy.RETAIN
      : RemovalPolicy.DESTROY;

    const bucket = new s3.Bucket(this, 'ReportsBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      versioned: props.persistent,
      removalPolicy: removal,
      autoDeleteObjects: !props.persistent,
      lifecycleRules: [
        {
          id: 'ArchiveRawReports',
          prefix: 'pos/',
          transitions: [
            {
              storageClass: s3.StorageClass.INFREQUENT_ACCESS,
              transitionAfter: Duration.days(30),
            },
          ],
          expiration: Duration.days(props.persistent ? 365 : 30),
        },
      ],
    });

    const table = (name: string) =>
      new dynamodb.Table(this, name, {
        partitionKey: {
          name: 'flightId',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'timestamp',
          type: dynamodb.AttributeType.STRING,
        },
        billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
        encryption: dynamodb.TableEncryption.AWS_MANAGED,
        pointInTimeRecovery: props.persistent,
        removalPolicy: removal,
      });

    const parsed = table('ParsedReports');
    const results = table('CalculationResults');

    const dlq = new sqs.Queue(this, 'CalculationDlq', {
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      retentionPeriod: Duration.days(14),
    });

    const queue = new sqs.Queue(this, 'CalculationQueue', {
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      visibilityTimeout: Duration.seconds(180),
      retentionPeriod: Duration.days(4),
      deadLetterQueue: {
        queue: dlq,
        maxReceiveCount: 5,
      },
    });

    const code = lambda.Code.fromAsset(
      path.join(__dirname, '../../application/FlightReportProcessor'),
      {
        bundling: {
          image: lambda.Runtime.DOTNET_8.bundlingImage,
          command: [
            '/bin/sh',
            '-c',
            'dotnet publish -c Release -o /asset-output',
          ],
        },
      },
    );

    const fn = (
      name: string,
      handler: string,
      timeout: number,
      environment: Record<string, string>,
    ) =>
      new lambda.Function(this, name, {
        runtime: lambda.Runtime.DOTNET_8,
        architecture: lambda.Architecture.ARM_64,
        handler: `FlightReportProcessor.${handler}::Handler`,
        code,
        memorySize: 512,
        timeout: Duration.seconds(timeout),
        environment,
        logRetention: logs.RetentionDays.ONE_MONTH,
        tracing: lambda.Tracing.ACTIVE,
      });

    const ingestion = fn('IngestionFunction', 'IngestionFunction', 20, {
      BUCKET_NAME: bucket.bucketName,
    });

    const parser = fn('ParserFunction', 'ParserFunction', 60, {
      PARSED_TABLE_NAME: parsed.tableName,
      QUEUE_URL: queue.queueUrl,
    });

    const calculator = fn('CalculatorFunction', 'CalculatorFunction', 120, {
      BUCKET_NAME: bucket.bucketName,
      PARSED_TABLE_NAME: parsed.tableName,
      RESULT_TABLE_NAME: results.tableName,
    });

    const status = fn('StatusFunction', 'StatusFunction', 20, {
      PARSED_TABLE_NAME: parsed.tableName,
      RESULT_TABLE_NAME: results.tableName,
    });

    bucket.grantPut(ingestion, 'pos/*');
    bucket.grantRead(parser, 'pos/*');
    bucket.grantPut(parser, 'attachment/*');
    parsed.grantWriteData(parser);
    queue.grantSendMessages(parser);

    bucket.grantRead(calculator, 'attachment/*');
    bucket.grantPut(calculator, 'results/*');
    parsed.grantReadData(calculator);
    results.grantWriteData(calculator);

    parsed.grantReadData(status);
    results.grantReadData(status);

    parser.addEventSource(
      new sources.S3EventSource(bucket, {
        events: [s3.EventType.OBJECT_CREATED],
        filters: [{ prefix: 'pos/' }],
      }),
    );

    calculator.addEventSource(
      new sources.SqsEventSource(queue, {
        batchSize: 5,
        maxBatchingWindow: Duration.seconds(5),
        reportBatchItemFailures: true,
        maxConcurrency: 5,
      }),
    );

    const api = new apigw.RestApi(this, 'Api', {
      deployOptions: {
        stageName: 'v1',
        tracingEnabled: true,
        loggingLevel: apigw.MethodLoggingLevel.INFO,
        dataTraceEnabled: false,
        metricsEnabled: true,
      },
    });

    api.root
      .addResource('pos-reports')
      .addMethod('POST', new apigw.LambdaIntegration(ingestion));

    api.root
      .addResource('status')
      .addResource('{flightId}')
      .addMethod('GET', new apigw.LambdaIntegration(status));

    new CfnOutput(this, 'ApiUrl', { value: api.url });
    new CfnOutput(this, 'BucketName', { value: bucket.bucketName });
    new CfnOutput(this, 'DlqUrl', { value: dlq.queueUrl });
  }
}
